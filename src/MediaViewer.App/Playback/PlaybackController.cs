using System.Runtime.InteropServices.WindowsRuntime;
using FFmpegInteropX;
using MediaViewer.Imaging.Graphics;
using MediaViewer.Imaging.Rendering;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;

namespace MediaViewer.App.Playback;

internal enum PlaybackState
{
    Closed,
    Opening,
    Playing,
    Paused,
    /// <summary>ユーザーが停止した（先頭へ戻した）状態。</summary>
    Stopped,
    /// <summary>自然に終端へ達した状態。次へ移動もリピートもしない（仕様 §7.2）。</summary>
    Ended,
    Failed,
}

internal sealed record AudioTrackItem(int Index, string Label);

internal sealed record OpenedMedia(
    bool HasVideo,
    int VideoWidth,
    int VideoHeight,
    int RotationDegrees,
    byte[]? AlbumArt,
    string DecoderDescription);

/// <summary>
/// 動画・音声の再生。Windows の MediaPlayer と FFmpegInteropX（FFmpegによるデコード）を使う。
/// 映像はフレームサーバーモードで <see cref="VideoFrameTarget"/> へコピーし、画像と同じ描画経路で表示する。
/// MediaPlayer のインスタンスはファイルをまたいで使い回す（仕様 §7.5）。
/// </summary>
internal sealed class PlaybackController : IDisposable
{
    private static readonly Guid MfMtVideoRotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");

    private readonly MediaPlayer _player;
    private readonly ContentRenderer _renderer;
    private readonly GraphicsDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _frameLock = new();

    private FFmpegMediaSource? _source;
    private VideoFrameTarget? _frame;
    private int _generation;

    public PlaybackState State { get; private set; } = PlaybackState.Closed;
    public bool HasVideo { get; private set; }
    public IReadOnlyList<AudioTrackItem> AudioTracks { get; private set; } = [];

    /// <summary>状態が変わった（UIスレッドで発生）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>再生中のエラー（UIスレッドで発生）。</summary>
    public event EventHandler<string>? PlaybackFailed;

    public PlaybackController(ContentRenderer renderer, DispatcherQueue dispatcher)
    {
        _renderer = renderer;
        _device = renderer.Device;
        _dispatcher = dispatcher;
        _player = new MediaPlayer
        {
            AutoPlay = false,
            IsVideoFrameServerEnabled = true,
            Volume = 1.0,
        };
        _player.CommandManager.IsEnabled = false;
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.MediaEnded += (_, _) => Post(() => SetState(PlaybackState.Ended));
        _player.MediaFailed += (_, e) => Post(() =>
        {
            SetState(PlaybackState.Failed);
            PlaybackFailed?.Invoke(this, $"再生できませんでした（{e.Error}）。{e.ErrorMessage}");
        });
    }

    /// <summary>現在の映像フレーム（描画用）。映像がない場合は null。</summary>
    public VideoFrameTarget? Frame
    {
        get { lock (_frameLock) return _frame; }
    }

    public TimeSpan Position => State == PlaybackState.Closed ? TimeSpan.Zero : _player.PlaybackSession.Position;
    public TimeSpan Duration => _source?.Duration ?? TimeSpan.Zero;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public int SelectedAudioTrack =>
        _source?.PlaybackItem is { } item ? item.AudioTracks.SelectedIndex : -1;

    public async Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken)
    {
        Close();
        var generation = ++_generation;
        SetState(PlaybackState.Opening);

        var config = new MediaSourceConfig();
        config.Video.VideoDecoderMode = VideoDecoderMode.Automatic;
        var source = await FFmpegMediaSource.CreateFromFileAsync(path, config);
        if (generation != _generation || cancellationToken.IsCancellationRequested)
        {
            source.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }
        _source = source;

        var video = source.VideoStreams.FirstOrDefault();
        HasVideo = video is not null;
        var (width, height) = video is null ? (0, 0) : DisplaySize(video);

        await source.OpenWithMediaPlayerAsync(_player);
        if (generation != _generation)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var item = source.PlaybackItem;
        AudioTracks = BuildTrackList(source);
        // ファイル内で最初の音声トラックを選ぶ。FFmpegInteropX は既定（default）指定のトラックを
        // 先頭に並べ替えるため、ストリーム番号順の先頭を明示的に選択する（仕様 §7.3）
        if (AudioTracks.Count > 0 && item.AudioTracks.Count == AudioTracks.Count)
        {
            var first = AudioTracks[0].Index;
            if (item.AudioTracks.SelectedIndex != first) item.AudioTracks.SelectedIndex = first;
        }

        var rotation = HasVideo ? await ReadRotationAsync(path, item) : 0;
        if (generation != _generation)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (HasVideo)
        {
            lock (_frameLock)
            {
                _frame = VideoFrameTarget.Create(_device, width, height);
            }
        }

        byte[]? art = null;
        if (!HasVideo && source.HasThumbnail)
        {
            try
            {
                using var thumb = source.ExtractThumbnail();
                art = thumb?.Buffer?.ToArray();
            }
            catch (Exception)
            {
                art = null;
            }
        }

        var decoder = video is null
            ? $"音声: {source.CurrentAudioStream?.CodecName} ({source.CurrentAudioStream?.DecoderEngine})"
            : $"映像: {video.CodecName} {video.PixelWidth}x{video.PixelHeight} ({video.DecoderEngine}, HW: {video.HardwareDecoderStatus})";

        _player.Play();
        SetState(PlaybackState.Playing);
        return new OpenedMedia(HasVideo, width, height, rotation, art, decoder);
    }

    private static (int Width, int Height) DisplaySize(VideoStreamInfo video)
    {
        var w = video.PixelWidth;
        var h = video.PixelHeight;
        // 非正方ピクセル（アナモルフィック）は表示アスペクト比に合わせる
        if (video.DisplayAspectRatio > 0 && h > 0)
        {
            var displayWidth = (int)Math.Round(h * video.DisplayAspectRatio);
            if (Math.Abs(displayWidth - w) > 1) w = displayWidth;
        }
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// 動画の表示回転（時計回りの角度）。FFmpegInteropX は回転情報を公開しないため、
    /// Windows のプロパティシステム（MP4/MOV 等の表示行列）から取得し、なければ Media Foundation の属性を見る。
    /// フレームサーバーの出力は回転前のフレームなので、描画側で回転を適用する。
    /// </summary>
    private static async Task<int> ReadRotationAsync(string path, MediaPlaybackItem item)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            var degrees = (int)props.Orientation;
            if (degrees is 90 or 180 or 270) return degrees;
        }
        catch (Exception)
        {
            // プロパティハンドラーがない形式（MKV等）は次の方法へ
        }
        try
        {
            if (item.VideoTracks.Count > 0
                && item.VideoTracks[0].GetEncodingProperties().Properties.TryGetValue(MfMtVideoRotation, out var value)
                && value is IConvertible c)
            {
                // MF_MT_VIDEO_ROTATION は反時計回りの角度
                return (360 - c.ToInt32(null) % 360) % 360;
            }
        }
        catch (Exception)
        {
        }
        return 0;
    }

    /// <summary>
    /// 音声トラック一覧をファイル内の順序（ストリーム番号順）で作る。Index は MediaPlaybackItem.AudioTracks の位置。
    /// </summary>
    private static List<AudioTrackItem> BuildTrackList(FFmpegMediaSource source)
    {
        var ordered = source.AudioStreams
            .Select((stream, playerIndex) => (stream, playerIndex))
            .OrderBy(x => x.stream.StreamIndex)
            .ToList();
        var list = new List<AudioTrackItem>(ordered.Count);
        for (var n = 0; n < ordered.Count; n++)
        {
            var s = ordered[n].stream;
            // 取得できた情報だけを表示し、未設定情報は推測しない（仕様 §7.3）。
            // タイトル未設定のとき FFmpegInteropX は言語名を名前に入れるため、重複は省く
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.Name) && s.Name != s.Language) parts.Add(s.Name);
            if (!string.IsNullOrWhiteSpace(s.Language)) parts.Add(s.Language);
            if (!string.IsNullOrWhiteSpace(s.CodecName)) parts.Add(s.CodecName.ToUpperInvariant());
            if (s.Channels > 0) parts.Add($"{s.Channels}ch");
            var label = $"トラック {n + 1}" + (parts.Count > 0 ? "：" + string.Join(" / ", parts) : "");
            list.Add(new AudioTrackItem(ordered[n].playerIndex, label));
        }
        return list;
    }

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        // Media Foundation のスレッドで呼ばれる
        lock (_frameLock)
        {
            var frame = _frame;
            if (frame is null) return;
            try
            {
                _renderer.RenderWith(() => sender.CopyFrameToVideoSurface(frame.Surface));
            }
            catch (Exception)
            {
                // 1フレームのコピー失敗では再生を止めない
            }
        }
    }

    public void Play()
    {
        if (_source is null) return;
        // 終端・停止からの再生は先頭から（仕様 §7.1, §7.2）
        if (State is PlaybackState.Ended or PlaybackState.Stopped)
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
        }
        _player.Play();
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (_source is null || State != PlaybackState.Playing) return;
        _player.Pause();
        SetState(PlaybackState.Paused);
    }

    public void TogglePlayPause()
    {
        if (State == PlaybackState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        if (_source is null) return;
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_source is null) return;
        var duration = Duration;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;
        _player.PlaybackSession.Position = position;
        if (State is PlaybackState.Ended or PlaybackState.Stopped && position < duration)
        {
            SetState(PlaybackState.Paused);
        }
    }

    public void SeekBy(TimeSpan delta) => Seek(Position + delta);

    public void SelectAudioTrack(int index)
    {
        var item = _source?.PlaybackItem;
        if (item is null || index < 0 || index >= item.AudioTracks.Count) return;
        // 再生位置と再生状態はそのまま維持される
        item.AudioTracks.SelectedIndex = index;
    }

    /// <summary>再生を止めてデコード資源を解放する（ファイル切替前、仕様 §3.3）。</summary>
    public void Close()
    {
        _generation++;
        if (_source is not null)
        {
            _player.Pause();
            _player.Source = null;
        }
        lock (_frameLock)
        {
            _frame?.Dispose();
            _frame = null;
        }
        _source?.Dispose();
        _source = null;
        HasVideo = false;
        AudioTracks = [];
        SetState(PlaybackState.Closed);
    }

    private void SetState(PlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action)
    {
        var generation = _generation;
        _dispatcher.TryEnqueue(() =>
        {
            if (generation == _generation) action();
        });
    }

    public void Dispose()
    {
        Close();
        _player.Dispose();
    }
}
