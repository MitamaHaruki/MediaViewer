using System.Numerics;
using MediaViewer.App.Interop;
using MediaViewer.App.Playback;
using MediaViewer.App.Services;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Media;
using MediaViewer.Core.Navigation;
using MediaViewer.Core.Settings;
using MediaViewer.Core.Viewport;
using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Decoding;
using MediaViewer.Imaging.Graphics;
using MediaViewer.Imaging.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaViewer.App;

/// <summary>
/// メイン画面。読込み・表示・再生の調停を行う。入力処理は MainWindow.Input.cs、
/// コントロールバー・メニュー・フルスクリーンは MainWindow.Controls.cs。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string AppName = "メディアビューワー";
    private const double AudioCaptionHeight = 96;
    private const double AlbumArtMaxExtent = 720;
    private const int AlbumArtMaxPixels = 1024;

    private readonly ViewportController _viewport = new();
    private readonly DispatcherQueueTimer _positionTimer;

    // 起動を軽くするため、描画・再生は最初にファイルを開くときに初期化する（仕様 §11.1）
    private GraphicsDevice? _device;
    private ContentRenderer? _renderer;
    private PlaybackController? _playback;
    private DisplayColorMonitor? _displayMonitor;

    private LoadedImage? _image; // 表示中の画像またはアルバムアート
    private string? _currentPath;
    private MediaKind _currentKind = MediaKind.Unknown;
    private NavigationFilter _filter = NavigationFilter.All;
    private NavigationNeighbors _neighbors;
    private int _openGeneration;
    private CancellationTokenSource? _openCts;
    private string _mediaInfo = "";

    public MainWindow(SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ApplyCaptionButtonColors();
        AppWindow.Title = AppName;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
        _positionTimer.Tick += (_, _) => UpdatePosition();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => OnHideTimerTick();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seekDragging = true), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seekDragging = false), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seekDragging = false), true);

        _viewport.Changed += (_, _) => ApplyView();
        Closed += OnClosed;
        Activated += (_, e) =>
        {
            // フォーカスがどこにもないとキー操作が届かないため、ビューワーへ戻す
            if (e.WindowActivationState != WindowActivationState.Deactivated
                && Content.XamlRoot is { } root
                && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is null)
            {
                FocusViewer();
            }
        };
        ControlBar.SizeChanged += (_, _) => UpdateCompactLayout();
        ApplySettings(settings);
        UpdateUi();
    }

    /// <summary>
    /// 最小化・最大化・閉じるボタンを暗い背景向けの色にする。Windows がライトモードだと
    /// 既定では黒いボタンになり、アプリの暗い背景に溶けて見えないため。
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        var bar = AppWindow.TitleBar;
        bar.PreferredTheme = TitleBarTheme.Dark;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        bar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>起動引数や関連付けから渡されたファイルを開く。</summary>
    public void OpenFromStartup(string path) => _ = OpenAsync(path);

    // ---- 描画の初期化 ----

    private ContentRenderer EnsureGraphics()
    {
        if (_renderer is not null) return _renderer;

        _device = GraphicsDevice.Create();
        _renderer = new ContentRenderer(_device);
        var (w, h) = PanelPixelSize();
        var swapChain = _renderer.GetOrCreateSwapChain(w, h);
        SwapChainPanelInterop.SetSwapChain(RenderPanel, swapChain.NativePointer);
        _renderer.Resize(w, h, RenderPanel.CompositionScaleX, RenderPanel.CompositionScaleY);

        _displayMonitor = new DisplayColorMonitor(AppWindow);
        _displayMonitor.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateDisplayColor);
        _renderer.SetDisplay(_displayMonitor.Read());

        _playback = new PlaybackController(_renderer, DispatcherQueue)
        {
            Volume = VolumeSlider.Value / 100.0,
            IsMuted = _isMuted,
        };
        _playback.StateChanged += (_, _) => UpdateUi();
        _playback.PlaybackFailed += (_, message) => ShowMessage(message, InfoBarSeverity.Error);
        _renderer.DeviceLost += (_, _) => DispatcherQueue.TryEnqueue(RecoverFromDeviceLost);
        return _renderer;
    }

    private bool _recoveringDevice;

    /// <summary>
    /// GPUデバイスが失われたら、描画・再生を作り直して表示中のファイルを開き直す。
    /// 動画・音声は再生位置と再生中かどうかを引き継ぐ。
    /// </summary>
    private async void RecoverFromDeviceLost()
    {
        if (_recoveringDevice) return;
        _recoveringDevice = true;
        AppLog.Warn("GPUデバイスの消失を検出しました。描画を作り直します。");
        var path = _currentPath;
        var wasPlayable = IsPlayable;
        var position = _playback?.Position ?? TimeSpan.Zero;
        var wasPlaying = _playback?.State == PlaybackState.Playing;
        try
        {
            ReleaseGraphics();
            EnsureGraphics();
            if (path is null)
            {
                _renderer!.Render();
                return;
            }
            await OpenAsync(path);
            if (wasPlayable && IsPlayable && _currentPath == path)
            {
                _playback!.Seek(position);
                if (!wasPlaying) _playback.Pause();
            }
            ShowMessage("グラフィックスの再初期化を行いました（GPUのリセットまたはドライバー更新を検出）。", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("描画の再初期化に失敗しました", ex);
            ShowMessage("グラフィックスを再初期化できませんでした。アプリを再起動してください。", InfoBarSeverity.Error);
        }
        finally
        {
            _recoveringDevice = false;
        }
    }

    /// <summary>描画・再生の資源をすべて解放する（デバイス消失時・終了時）。</summary>
    private void ReleaseGraphics()
    {
        _openCts?.Cancel();
        try { _renderer?.SetContent(null, SourceColor.Srgb, Matrix3x2.Identity); } catch (Exception) { }
        _playback?.Dispose();
        _playback = null;
        _image?.Dispose();
        _image = null;
        _displayMonitor?.Dispose();
        _displayMonitor = null;
        _renderer?.Dispose();
        _renderer = null;
        _device?.Dispose();
        _device = null;
        _currentKind = MediaKind.Unknown;
        _viewport.ClearContent();
    }

    private void UpdateDisplayColor()
    {
        if (_renderer is null || _displayMonitor is null) return;
        _renderer.SetDisplay(_displayMonitor.Read());
        _renderer.Render();
    }

    private (int Width, int Height) PanelPixelSize() =>
        (Math.Max(1, (int)Math.Ceiling(RenderPanel.ActualWidth * RenderPanel.CompositionScaleX)),
         Math.Max(1, (int)Math.Ceiling(RenderPanel.ActualHeight * RenderPanel.CompositionScaleY)));

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceSize();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => UpdateSurfaceSize();

    private void UpdateSurfaceSize()
    {
        if (_renderer is null) return;
        var (w, h) = PanelPixelSize();
        _renderer.Resize(w, h, RenderPanel.CompositionScaleX, RenderPanel.CompositionScaleY);
        UpdateViewportSize();
        ApplyView();
    }

    private void UpdateViewportSize()
    {
        var height = RenderPanel.ActualHeight;
        if (_currentKind == MediaKind.Audio) height = Math.Max(0, height - AudioCaptionHeight);
        _viewport.SetViewport(RenderPanel.ActualWidth, height, RenderPanel.CompositionScaleX);
    }

    /// <summary>表示状態（倍率・位置）を描画へ反映する。画像と動画で共通。</summary>
    private void ApplyView()
    {
        if (_renderer is null) return;
        var s = RenderPanel.CompositionScaleX;
        _renderer.SetView((float)(_viewport.Scale * s), (float)(_viewport.OffsetX * s), (float)(_viewport.OffsetY * s));
        _renderer.Render();
        FitButton.IsEnabled = IsZoomable && !_viewport.IsAtFit;
    }

    // ---- ファイルを開く ----

    private async Task OpenAsync(string path)
    {
        var generation = ++_openGeneration;
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        HideMessage();
        var renderer = EnsureGraphics();
        CloseCurrent();

        _currentPath = path;
        var kind = MediaKinds.FromPath(path);
        UpdateTitle();
        RefreshNeighbors();
        LoadingRing.IsActive = true;
        Placeholder.Visibility = Visibility.Collapsed;

        try
        {
            if (kind is MediaKind.Image or MediaKind.Unknown)
            {
                LoadedImage? image = null;
                try
                {
                    image = await Task.Run(() => WicImageLoader.Load(_device!, path, ct), ct);
                }
                catch (Exception) when (kind == MediaKind.Unknown && !ct.IsCancellationRequested)
                {
                    // 拡張子が不明なファイルは、画像として読めなければ動画・音声として試す（仕様 §3.1）
                }
                if (image is not null)
                {
                    if (generation != _openGeneration)
                    {
                        image.Dispose();
                        return;
                    }
                    ShowImage(image);
                    return;
                }
            }

            var opened = await _playback!.OpenAsync(path, ct);
            if (generation != _openGeneration) return;
            if (opened.HasVideo)
            {
                ShowVideo(opened);
            }
            else
            {
                await ShowAudioAsync(opened, path, generation, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;
            _currentKind = MediaKind.Unknown;
            ShowMessage(DescribeOpenError(path, ex), InfoBarSeverity.Error);
        }
        finally
        {
            if (generation == _openGeneration)
            {
                LoadingRing.IsActive = false;
                UpdateUi();
                FocusViewer();
                // 前のファイルのGPUメモリを返す（新しい表示内容は保持される）
                _renderer?.TrimMemory();
            }
        }
    }

    private static string DescribeOpenError(string path, Exception ex)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => $"「{name}」が見つかりません。移動または削除された可能性があります。",
            UnauthorizedAccessException => $"「{name}」へのアクセスが拒否されました。",
            NotSupportedException => $"「{name}」を開けません：{ex.Message}",
            _ => $"「{name}」を開けませんでした。対応していない形式か、ファイルが壊れている可能性があります。",
        };
    }

    /// <summary>表示中のコンテンツを止めて資源を解放する（仕様 §3.3）。</summary>
    private void CloseCurrent()
    {
        _renderer?.SetContent(null, SourceColor.Srgb, Matrix3x2.Identity);
        _playback?.Close();
        _image?.Dispose();
        _image = null;
        _currentKind = MediaKind.Unknown;
        _mediaInfo = "";
        _viewport.ClearContent();
        MusicNoteIcon.Visibility = Visibility.Collapsed;
        AudioFileName.Visibility = Visibility.Collapsed;
        _renderer?.Render();
    }

    private void ShowImage(LoadedImage image)
    {
        _image = image;
        _currentKind = MediaKind.Image;
        _renderer!.SetContent(image.Image, image.Color, image.Orientation.ToUprightMatrix(image.PixelWidth, image.PixelHeight));
        UpdateViewportSize();
        _viewport.SetContent(image.UprightWidth, image.UprightHeight, limitFitToActualSize: true);
        _mediaInfo = $"画像 {image.PixelWidth}x{image.PixelHeight}、向き {image.Orientation}、色 {image.Color.Label}"
            + (image.IsHighBitDepth ? "、高ビット深度" : "") + (image.FrameCount > 1 ? $"、{image.FrameCount}フレーム" : "")
            + (image.Color.Note is { } colorNote ? $"（{colorNote}）" : "");

        if (image.Color.UsesFallback)
        {
            ShowMessage($"埋め込みICCプロファイルを使用できないため、sRGBとして表示しています。（{image.Color.Note}）", InfoBarSeverity.Warning);
        }
        else if (image.Color.Kind is SourceColorKind.CmykConverted or SourceColorKind.AssumedSrgb && image.Color.Note is { } note)
        {
            // 既定プロファイルでの変換や、未対応の色指定を sRGB とみなした場合は黙らずに知らせる（仕様 §0-8）
            ShowMessage(note + "。", InfoBarSeverity.Informational);
        }
        else if (image.FrameCount > 1)
        {
            // 複数ページ・アニメーションは初期版では最初のフレームのみ（仕様決定事項 #10）
            ShowMessage($"複数フレーム（{image.FrameCount}）の画像です。最初のフレームのみ表示しています。", InfoBarSeverity.Informational);
        }
    }

    private void ShowVideo(OpenedMedia media)
    {
        var frame = _playback!.Frame;
        if (frame is null) return;
        _currentKind = MediaKind.Video;
        var upright = RotationMatrix(media.RotationDegrees, frame.Width, frame.Height, out var w, out var h);
        _renderer!.SetContent(frame.Image, SourceColor.Srgb, upright);
        UpdateViewportSize();
        _viewport.SetContent(w, h, limitFitToActualSize: false);
        _mediaInfo = media.DecoderDescription + (media.RotationDegrees != 0 ? $"、回転 {media.RotationDegrees}°" : "");
        RebuildAudioTrackMenus();
    }

    private async Task ShowAudioAsync(OpenedMedia media, string path, int generation, CancellationToken ct)
    {
        _currentKind = MediaKind.Audio;
        AudioFileName.Text = Path.GetFileName(path);
        AudioFileName.Visibility = Visibility.Visible;
        _mediaInfo = media.DecoderDescription;
        RebuildAudioTrackMenus();
        UpdateViewportSize();

        LoadedImage? art = null;
        if (media.AlbumArt is { Length: > 0 } bytes)
        {
            try
            {
                // アルバムアートも画像と同じデコード・色管理経路を使う（仕様 §5.2-9）
                art = await Task.Run(() => WicImageLoader.LoadFromMemory(_device!, bytes, AlbumArtMaxPixels, ct), ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                art = null;
            }
        }
        if (generation != _openGeneration)
        {
            art?.Dispose();
            return;
        }
        if (art is null)
        {
            MusicNoteIcon.Visibility = Visibility.Visible;
            _renderer!.Render();
            return;
        }
        _image = art;
        _renderer!.SetContent(art.Image, art.Color, art.Orientation.ToUprightMatrix(art.PixelWidth, art.PixelHeight));
        _viewport.SetContent(art.UprightWidth, art.UprightHeight, limitFitToActualSize: false, AlbumArtMaxExtent);
    }

    /// <summary>動画の回転メタデータ（時計回りの角度）を表示用の変換行列にする。</summary>
    private static Matrix3x2 RotationMatrix(int degrees, int width, int height, out int uprightWidth, out int uprightHeight)
    {
        var orientation = degrees switch
        {
            90 => ExifOrientation.Rotate90,
            180 => ExifOrientation.Rotate180,
            270 => ExifOrientation.Rotate270,
            _ => ExifOrientation.Normal,
        };
        uprightWidth = orientation.SwapsAxes() ? height : width;
        uprightHeight = orientation.SwapsAxes() ? width : height;
        return orientation.ToUprightMatrix(width, height);
    }

    // ---- 前後移動 ----

    private async void RefreshNeighbors()
    {
        var path = _currentPath;
        var filter = _filter;
        if (path is null)
        {
            _neighbors = NavigationNeighbors.None;
            UpdateNavigationButtons();
            return;
        }
        var neighbors = await Task.Run(() => FolderNavigator.FindNeighbors(path, filter));
        if (path != _currentPath || filter != _filter) return;
        _neighbors = neighbors;
        UpdateNavigationButtons();
    }

    private async void Navigate(bool forward)
    {
        var path = _currentPath;
        if (path is null) return;
        // 移動の都度フォルダを列挙し直し、追加・削除を反映する（仕様決定事項 #9）
        var neighbors = await Task.Run(() => FolderNavigator.FindNeighbors(path, _filter));
        if (path != _currentPath) return;
        _neighbors = neighbors;
        UpdateNavigationButtons();
        var target = forward ? neighbors.Next : neighbors.Previous;
        if (target is null) return; // 先頭・末尾では循環しない（仕様 §4.3-3）
        await OpenAsync(target);
    }

    private void UpdateNavigationButtons()
    {
        PreviousButton.IsEnabled = MenuPrevious.IsEnabled = _neighbors.Previous is not null;
        NextButton.IsEnabled = MenuNext.IsEnabled = _neighbors.Next is not null;
    }

    private void SetFilter(NavigationFilter filter)
    {
        _filter = filter;
        foreach (var item in new[] { MenuFilterAll, MenuFilterImages, MenuFilterVideos, MenuFilterAudio, BarFilterAll, BarFilterImages, BarFilterVideos, BarFilterAudio })
        {
            item.IsChecked = (string)item.Tag == filter.ToString();
        }
        FilterText.Text = filter switch
        {
            NavigationFilter.Images => "画像のみ",
            NavigationFilter.Videos => "動画のみ",
            NavigationFilter.Audio => "音声のみ",
            _ => "すべて",
        };
        // フィルター変更の瞬間にフォルダを再走査する（仕様 §4.2）。表示中のファイルはそのまま
        RefreshNeighbors();
        SaveSettings(s => s.NavigationFilter = filter);
    }

    // ---- 状態表示 ----

    private bool IsZoomable => _currentKind is MediaKind.Image or MediaKind.Video && _viewport.HasContent;
    private bool IsPlayable => _currentKind is MediaKind.Video or MediaKind.Audio && _playback is not null;

    private void UpdateTitle()
    {
        var name = _currentPath is null ? null : Path.GetFileName(_currentPath);
        TitleFileName.Text = name ?? "";
        AppWindow.Title = name is null ? AppName : $"{name} - {AppName}";
    }

    private void UpdateUi()
    {
        Placeholder.Visibility = _currentPath is null ? Visibility.Visible : Visibility.Collapsed;

        var playable = IsPlayable;
        var state = _playback?.State ?? PlaybackState.Closed;
        PlayPauseButton.IsEnabled = StopButton.IsEnabled = SeekSlider.IsEnabled = playable;
        MenuPlayPause.IsEnabled = MenuStop.IsEnabled = playable;
        PlayPauseIcon.Glyph = state == PlaybackState.Playing ? "" : "";
        FitButton.IsEnabled = IsZoomable && !_viewport.IsAtFit;

        if (playable && state == PlaybackState.Playing) _positionTimer.Start();
        else _positionTimer.Stop();
        UpdatePosition();
    }

    private bool _updatingSeek;

    private void UpdatePosition()
    {
        if (!IsPlayable)
        {
            _updatingSeek = true;
            SeekSlider.Value = 0;
            _updatingSeek = false;
            PositionText.Text = DurationText.Text = "0:00";
            return;
        }
        var duration = _playback!.Duration;
        var position = _playback.State == PlaybackState.Ended ? duration : _playback.Position;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(0.1, duration.TotalSeconds);
        if (!_seekDragging) SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, SeekSlider.Maximum);
        _updatingSeek = false;
        PositionText.Text = FormatTime(position);
        DurationText.Text = FormatTime(duration);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }

    private void HideMessage() => MessageBar.IsOpen = false;

    private void FocusViewer(FocusState state = FocusState.Programmatic) => InputLayer.Focus(state);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _volumeSaveTimer?.Stop();
        SaveWindowPlacementOnClose();
        _positionTimer.Stop();
        _hideTimer.Stop();
        ReleaseGraphics();
    }
}
