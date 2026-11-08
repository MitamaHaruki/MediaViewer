using MediaViewer.App.Playback;
using MediaViewer.Core.Media;
using MediaViewer.Core.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaViewer.App;

/// <summary>
/// スライドショー（画像のみ）と連続再生（動画のみ・音声のみ）（仕様決定事項 D14）。
/// 前後移動の対象が「画像のみ」ならスライドショー、「動画のみ」「音声のみ」なら連続再生として動き、
/// 「すべて」のときは使えない。最後のファイルまで進んだら、リピートが ON なら先頭へ戻り、OFF なら止まる。
/// 連続再生中のリピートは「一覧の繰り返し」になり、1つのファイルの繰り返しはしない。
/// </summary>
public sealed partial class MainWindow
{
    private enum AutoPlayMode
    {
        None,
        Slideshow,
        Continuous,
    }

    private bool _autoPlay;
    private DispatcherQueueTimer? _slideTimer;
    private TimeSpan _slideInterval = TimeSpan.FromSeconds(5);

    /// <summary>今の前後移動の対象で使える自動送りの種類。</summary>
    private AutoPlayMode AvailableAutoPlay => _filter switch
    {
        NavigationFilter.Images => AutoPlayMode.Slideshow,
        NavigationFilter.Videos or NavigationFilter.Audio => AutoPlayMode.Continuous,
        _ => AutoPlayMode.None,
    };

    private bool IsContinuousPlaying => _autoPlay && AvailableAutoPlay == AutoPlayMode.Continuous;

    private void InitializeAutoPlay()
    {
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.IsRepeating = false;
        _slideTimer.Tick += (_, _) => AdvanceAutoPlay();
        UpdateAutoPlayUi();
    }

    private void OnAutoPlayClick(object sender, RoutedEventArgs e) => SetAutoPlay(AutoPlayButton.IsChecked == true);

    private void OnAutoPlayMenuClick(object sender, RoutedEventArgs e) => SetAutoPlay(MenuAutoPlay.IsChecked);

    private void ToggleAutoPlay() => SetAutoPlay(!_autoPlay);

    private void SetAutoPlay(bool on)
    {
        var mode = AvailableAutoPlay;
        if (mode == AutoPlayMode.None) on = false;
        if (_autoPlay == on)
        {
            UpdateAutoPlayUi();
            return;
        }
        _autoPlay = on;
        ApplyRepeatToPlayback();
        _slideTimer?.Stop();
        UpdateAutoPlayUi();
        UpdateUi();
        if (!on) return;

        // 表示中のファイルが対象外（例：連続再生なのに画像を表示中）なら、すぐ次の対象へ進む
        var current = MediaKinds.FromPath(_currentPath ?? "");
        if (_currentPath is null || !FolderNavigator.Matches(_filter, current))
        {
            AdvanceAutoPlay();
            return;
        }
        if (mode == AutoPlayMode.Slideshow)
        {
            RestartSlideTimer();
        }
        else if (_playback is { } playback)
        {
            if (playback.State == PlaybackState.Ended) AdvanceAutoPlay();
            else if (playback.State is PlaybackState.Paused or PlaybackState.Stopped) playback.Play();
        }
    }

    /// <summary>連続再生中は1つのファイルの繰り返しをせず、一覧の最後で先頭へ戻るかどうかに使う。</summary>
    private void ApplyRepeatToPlayback()
    {
        if (_playback is not null) _playback.IsRepeatEnabled = _isRepeat && !IsContinuousPlaying;
    }

    private void RestartSlideTimer()
    {
        if (_slideTimer is null) return;
        _slideTimer.Stop();
        if (!_autoPlay || AvailableAutoPlay != AutoPlayMode.Slideshow) return;
        _slideTimer.Interval = _slideInterval;
        _slideTimer.Start();
    }

    /// <summary>次のファイルへ進む。最後なら、リピート ON で先頭へ戻り、OFF で止める。</summary>
    private async void AdvanceAutoPlay()
    {
        if (!_autoPlay) return;
        var path = _currentPath;
        var filter = _filter;
        string? target = null;
        if (path is not null)
        {
            var neighbors = await Task.Run(() => FolderNavigator.FindNeighbors(path, filter));
            if (!_autoPlay || path != _currentPath || filter != _filter) return;
            _neighbors = neighbors;
            UpdateNavigationButtons();
            target = neighbors.Next;
            if (target is null && _isRepeat)
            {
                var directory = Path.GetDirectoryName(path);
                var first = directory is null ? null : (await Task.Run(() => FolderNavigator.List(directory, filter))).FirstOrDefault();
                if (!_autoPlay || path != _currentPath) return;
                target = first;
            }
        }
        if (target is null)
        {
            SetAutoPlay(false);
            ShowMessage(AvailableAutoPlay == AutoPlayMode.Slideshow
                ? "最後の画像まで表示したため、スライドショーを終了しました。"
                : "最後のファイルまで再生したため、連続再生を終了しました。", InfoBarSeverity.Informational);
            return;
        }
        if (target == path && _playback is { } playback && IsPlayable)
        {
            // 対象が1つだけでリピート ON：同じファイルを先頭から
            playback.Seek(TimeSpan.Zero);
            playback.Play();
            return;
        }
        await OpenAsync(target);
    }

    /// <summary>連続再生中に開けなかったファイルを、エラーを見せてから飛ばすまでの時間。</summary>
    private static readonly TimeSpan SkipFailedDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// ファイルを開き終えたとき：スライドショーなら次の切り替えまでの時間を数え直す。
    /// 連続再生で開けなかった（再生の終わりが来ない）ファイルは、少し待ってから次へ進む。
    /// </summary>
    private void OnOpenedForAutoPlay()
    {
        if (!_autoPlay || _slideTimer is null) return;
        _slideTimer.Stop();
        if (AvailableAutoPlay == AutoPlayMode.Slideshow)
        {
            RestartSlideTimer();
        }
        else if (!IsPlayable)
        {
            _slideTimer.Interval = SkipFailedDelay;
            _slideTimer.Start();
        }
    }

    /// <summary>再生の状態が変わったとき：連続再生で最後まで再生したら次へ。</summary>
    private void OnPlaybackStateForAutoPlay()
    {
        if (IsContinuousPlaying && _playback?.State == PlaybackState.Ended)
        {
            DispatcherQueue.TryEnqueue(AdvanceAutoPlay);
        }
    }

    /// <summary>前後移動の対象を変えたとき：その対象で使えない自動送りは止める。</summary>
    private void OnFilterChangedForAutoPlay()
    {
        if (_autoPlay)
        {
            _autoPlay = false;
            _slideTimer?.Stop();
        }
        ApplyRepeatToPlayback();
        UpdateAutoPlayUi();
    }

    private void UpdateAutoPlayUi()
    {
        var mode = AvailableAutoPlay;
        var text = mode == AutoPlayMode.Continuous ? "連続再生" : "スライドショー";
        AutoPlayButton.IsEnabled = MenuAutoPlay.IsEnabled = mode != AutoPlayMode.None;
        AutoPlayButton.IsChecked = MenuAutoPlay.IsChecked = _autoPlay;
        AutoPlayIcon.Glyph = mode == AutoPlayMode.Continuous ? "" : "";
        MenuAutoPlay.Text = text;
        ToolTipService.SetToolTip(AutoPlayButton, mode == AutoPlayMode.None
            ? "スライドショー・連続再生（前後移動の対象を「画像のみ」「動画のみ」「音声のみ」にすると使えます）"
            : $"{text} (F5)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoPlayButton, text);
    }
}
