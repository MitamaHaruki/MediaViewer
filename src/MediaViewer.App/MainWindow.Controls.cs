using System.Reflection;
using MediaViewer.App.Playback;
using MediaViewer.Core.Media;
using MediaViewer.Core.Navigation;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace MediaViewer.App;

/// <summary>
/// コントロールバー・メニュー・仮想フルスクリーン・ダイアログ。
/// </summary>
public sealed partial class MainWindow
{
    private const string DeveloperText = "開発者：三玉春樹 / はるたまプロジェクト";
    private const string AccountLabel = "アカウント：";
    private const string AccountUrl = "https://x.com/MitamaHaruki";

    private static readonly SolidColorBrush BarBrushNormal = new(ColorHelper.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
    private static readonly SolidColorBrush BarBrushOverlay = new(ColorHelper.FromArgb(0xD9, 0x1C, 0x1C, 0x1C));

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private bool _isFullScreen;
    private bool _pointerOverBar;
    private bool _seekDragging;
    private bool _isMuted;
    private OverlappedPresenterState _restoreState = OverlappedPresenterState.Restored;
    private RectInt32 _restoreRect;

    // ---- ファイル ----

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add("*");
            var result = await picker.PickSingleFileAsync();
            if (result is not null && !string.IsNullOrEmpty(result.Path))
            {
                await OpenAsync(result.Path);
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"ファイルを選択できませんでした：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => RequestClose();

    private ConversionWindow? _conversionWindow;

    /// <summary>変換画面を開く（表示中のファイルを入力にする）。既に開いていれば前面に出す。</summary>
    private void OnConvertClick(object sender, RoutedEventArgs e)
    {
        if (_conversionWindow is null)
        {
            _conversionWindow = new ConversionWindow(_currentPath, OpenAsync);
            _conversionWindow.Closed += (_, _) => _conversionWindow = null;
        }
        else if (_currentPath is not null)
        {
            _conversionWindow.SetInput(_currentPath);
        }
        _conversionWindow.Activate();
    }

    // ---- 表示 ----

    private void OnFitClick(object sender, RoutedEventArgs e) => _viewport.ResetToFit();

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(!_isFullScreen);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Navigate(forward: false);

    private void OnNextClick(object sender, RoutedEventArgs e) => Navigate(forward: true);

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<NavigationFilter>(tag, out var filter))
        {
            SetFilter(filter);
        }
    }

    // ---- 再生 ----

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (IsPlayable) _playback!.TogglePlayPause();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (IsPlayable) _playback!.Stop();
        UpdatePosition();
    }

    private void OnSeekSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || !IsPlayable) return;
        _playback!.Seek(TimeSpan.FromSeconds(e.NewValue));
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void OnVolumeSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 音量・ミュートはファイルを切り替えても維持する（仕様 §3.3）
        if (_playback is not null) _playback.Volume = e.NewValue / 100.0;
        if (e.NewValue > 0 && _isMuted && !_applyingSettings) SetMuted(false);
        UpdateMuteIcon();
        ScheduleVolumeSave();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => SetMuted(!_isMuted);

    private void OnMuteMenuClick(object sender, RoutedEventArgs e) => SetMuted(MenuMute.IsChecked);

    private void OnVolumeUpClick(object sender, RoutedEventArgs e) => VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + VolumeStep);

    private void OnVolumeDownClick(object sender, RoutedEventArgs e) => VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - VolumeStep);

    private void SetMuted(bool muted)
    {
        var changed = _isMuted != muted;
        _isMuted = muted;
        if (_playback is not null) _playback.IsMuted = muted;
        MenuMute.IsChecked = muted;
        UpdateMuteIcon();
        if (changed) SaveSettings(s => s.IsMuted = muted);
    }

    private void UpdateMuteIcon()
    {
        MuteIcon.Glyph = _isMuted || VolumeSlider.Value <= 0 ? "" : "";
    }

    /// <summary>音声トラックが複数あるときだけ切替メニューを出す（仕様 §7.3）。</summary>
    private void RebuildAudioTrackMenus()
    {
        AudioTrackFlyout.Items.Clear();
        MenuAudioTracks.Items.Clear();
        var tracks = _playback?.AudioTracks ?? [];
        var selected = _playback?.SelectedAudioTrack ?? -1;
        var multiple = tracks.Count > 1;
        AudioTrackButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        MenuAudioTracks.IsEnabled = multiple;
        if (!multiple) return;

        foreach (var track in tracks)
        {
            AudioTrackFlyout.Items.Add(CreateTrackItem(track, selected, "BarTrack"));
            MenuAudioTracks.Items.Add(CreateTrackItem(track, selected, "MenuTrack"));
        }
    }

    private RadioMenuFlyoutItem CreateTrackItem(AudioTrackItem track, int selected, string group)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = track.Label,
            GroupName = group,
            IsChecked = track.Index == selected,
            Tag = track.Index,
        };
        item.Click += (_, _) =>
        {
            _playback?.SelectAudioTrack(track.Index);
            RebuildAudioTrackMenus();
        };
        return item;
    }

    // ---- 仮想フルスクリーン（仕様 §8） ----

    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen) return;
        if (on)
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                _restoreState = p.State;
            }
            _restoreRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            // 現在のモニターを覆うボーダーレス表示。排他フルスクリーンではない
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            AppTitleBar.Visibility = Visibility.Collapsed;
            Grid.SetRow(ViewerHost, 0);
            Grid.SetRowSpan(ViewerHost, 3);
            ControlBar.Background = BarBrushOverlay;
            ControlBar.MaxWidth = 1100;
            ControlBar.Margin = new Thickness(24, 0, 24, 24);
            ControlBar.CornerRadius = new CornerRadius(8);
            ControlBar.VerticalAlignment = VerticalAlignment.Bottom;
            FullScreenIcon.Glyph = "";
            _isFullScreen = true;
            ShowControlsTemporarily();
        }
        else
        {
            _isFullScreen = false;
            _hideTimer.Stop();
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            RestoreWindowPlacement();
            AppTitleBar.Visibility = Visibility.Visible;
            Grid.SetRow(ViewerHost, 1);
            Grid.SetRowSpan(ViewerHost, 1);
            ControlBar.Background = BarBrushNormal;
            ControlBar.MaxWidth = double.PositiveInfinity;
            ControlBar.Margin = new Thickness(0);
            ControlBar.CornerRadius = new CornerRadius(0);
            ControlBar.VerticalAlignment = VerticalAlignment.Stretch;
            FullScreenIcon.Glyph = "";
            SetControlBarVisible(true);
        }
    }

    /// <summary>
    /// フルスクリーン前の位置・サイズ・最大化状態に戻す。モニターの切断や解像度変更で
    /// 画面外に出る場合は、最も近いモニターの作業領域内に収める（仕様 §8-7）。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (_restoreState == OverlappedPresenterState.Maximized)
        {
            presenter.Maximize();
            return;
        }
        AppWindow.MoveAndResize(ClampToWorkArea(_restoreRect));
    }

    /// <summary>フルスクリーン中、操作があればバーを表示し、約5秒後に隠す（仕様 §8-3〜5）。</summary>
    private void ShowControlsTemporarily()
    {
        if (!_isFullScreen) return;
        SetControlBarVisible(true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTimerTick()
    {
        if (!_isFullScreen) return;
        // バー上にポインターがある間、シーク・タッチ操作中、メニュー表示中は隠さない
        if (_pointerOverBar || _seekDragging || _manipulating || IsAnyFlyoutOpen())
        {
            _hideTimer.Start();
            return;
        }
        SetControlBarVisible(false);
    }

    /// <summary>メニュー・ドロップダウンが開いているか（ツールチップは除く）。</summary>
    private bool IsAnyFlyoutOpen() =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => p.Child is not ToolTip);

    private void SetControlBarVisible(bool visible)
    {
        ControlBar.Opacity = visible ? 1 : 0;
        ControlBar.IsHitTestVisible = visible;
    }

    private void OnControlBarPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOverBar = true;

    private void OnControlBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverBar = false;
        ShowControlsTemporarily();
    }

    /// <summary>狭い画面では重要度の低い操作を隠し、メニューから使う（仕様 §2.1）。</summary>
    private void UpdateCompactLayout()
    {
        var width = ControlBar.ActualWidth;
        VolumeSlider.Visibility = width < 720 ? Visibility.Collapsed : Visibility.Visible;
        FilterButton.Visibility = width < 620 ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = width < 480 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- ダイアログ ----

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0) version = version[..plusIndex];

        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon-64.png")),
            Width = 48,
            Height = 48,
        });
        var names = new StackPanel();
        names.Children.Add(new TextBlock { Text = AppName, FontSize = 20, FontWeight = FontWeights.SemiBold });
        names.Children.Add(new TextBlock { Text = $"バージョン {version}", Opacity = 0.8 });
        header.Children.Add(names);
        panel.Children.Add(header);

        // 開発者表記は指定どおり表示する（仕様 §2.3）
        panel.Children.Add(new TextBlock { Text = DeveloperText, IsTextSelectionEnabled = true, Margin = new Thickness(0, 12, 0, 0) });
        var account = new StackPanel { Orientation = Orientation.Horizontal };
        account.Children.Add(new TextBlock { Text = AccountLabel, VerticalAlignment = VerticalAlignment.Center });
        account.Children.Add(new HyperlinkButton { Content = AccountUrl, NavigateUri = new Uri(AccountUrl), Padding = new Thickness(0) });
        panel.Children.Add(account);

        panel.Children.Add(new TextBlock
        {
            Text = "本ソフトウェアは GNU General Public License v3（またはそれ以降）で提供されます。"
                + "Windows App SDK とのリンクについて、GPLv3 第7条に基づく追加許可を付しています。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Opacity = 0.8,
        });
        var licenses = new HyperlinkButton { Content = "ライセンス情報（本体・外部ライブラリ）", Padding = new Thickness(0) };
        licenses.Click += (_, _) => OpenLicenseFile();
        panel.Children.Add(licenses);

        await ShowDialogAsync("このアプリについて", panel);
    }

    private void OpenLicenseFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (!File.Exists(path))
        {
            ShowMessage("ライセンス情報ファイルが見つかりません。", InfoBarSeverity.Warning);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>
        {
            $"表示先：{_renderer?.Display.Description ?? "（未初期化）"}",
            $"色変換経路：{_renderer?.ColorDescription ?? "（未初期化）"}",
            $"GPU：{(_device is null ? "（未初期化）" : _device.IsSoftware ? "ソフトウェア（WARP）" : "ハードウェア")}",
            $"表示中：{(_currentPath is null ? "なし" : Path.GetFileName(_currentPath))}（{_currentKind}）",
            $"詳細：{(_mediaInfo.Length > 0 ? _mediaInfo : "-")}",
            $"倍率：{_viewport.Scale / Math.Max(1e-9, _viewport.ActualSizeScale) * 100:0.#}%（フィット {_viewport.FitScale / Math.Max(1e-9, _viewport.ActualSizeScale) * 100:0.#}%）",
        };
        var text = new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
        };
        await ShowDialogAsync("色管理の診断情報", text);
    }

    private async Task ShowDialogAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "閉じる",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }
}
