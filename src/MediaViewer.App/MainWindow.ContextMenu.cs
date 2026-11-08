using System.Diagnostics;
using MediaViewer.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MediaViewer.App;

/// <summary>
/// ビューワーの右クリック（タッチでは長押し）メニュー（仕様決定事項 D16）。
/// 表示中のファイルの種類に合わせて、使えない項目は隠すか押せないようにする。
/// </summary>
public sealed partial class MainWindow
{
    private void OnViewerContextMenuOpening(object? sender, object e)
    {
        var hasFile = _currentPath is not null;
        var playable = IsPlayable;
        var autoPlay = AvailableAutoPlay;

        CtxShowInFolder.IsEnabled = CtxCopyPath.IsEnabled = hasFile;

        var playbackVisibility = playable || autoPlay != AutoPlayMode.None ? Visibility.Visible : Visibility.Collapsed;
        CtxPlaybackSeparator.Visibility = playbackVisibility;
        CtxPlayPause.Visibility = CtxStop.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        CtxRepeat.Visibility = playbackVisibility;
        CtxRepeat.IsChecked = _isRepeat;
        CtxAutoPlay.Visibility = autoPlay == AutoPlayMode.None ? Visibility.Collapsed : Visibility.Visible;
        CtxAutoPlay.Text = autoPlay == AutoPlayMode.Continuous ? "連続再生" : "スライドショー";
        CtxAutoPlay.IsChecked = _autoPlay;

        CtxFit.IsEnabled = IsZoomable && !_viewport.IsAtFit;
        CtxActualSize.IsEnabled = IsZoomable;
        CtxSidebar.IsChecked = _sidebarOpen;
        CtxFullScreen.IsChecked = _isFullScreen;
        CtxPrint.IsEnabled = CanPrint;
    }

    private void OnContextRepeatClick(object sender, RoutedEventArgs e) => SetRepeat(CtxRepeat.IsChecked);

    private void OnContextAutoPlayClick(object sender, RoutedEventArgs e) => SetAutoPlay(CtxAutoPlay.IsChecked);

    private void OnContextSidebarClick(object sender, RoutedEventArgs e) => SetSidebarOpen(CtxSidebar.IsChecked, save: true);

    private void OnContextFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(CtxFullScreen.IsChecked);

    /// <summary>画面の中央を基準に、画像・動画を等倍（1ピクセル＝画面の1ピクセル）で表示する。</summary>
    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        if (!IsZoomable) return;
        _viewport.ZoomTo(_viewport.ActualSizeScale, _viewport.ViewportWidth / 2, _viewport.ViewportHeight / 2);
    }

    /// <summary>エクスプローラーでフォルダを開き、表示中のファイルを選んだ状態にする。</summary>
    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not { } path) return;
        try
        {
            // PATH 上の同名ファイルを誤って起動しないよう、Windows フォルダの explorer.exe を直接指定する
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { Arguments = $"/select,\"{path}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ShowMessage($"エクスプローラーを開けませんでした：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not { } path) return;
        try
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ShowMessage($"クリップボードにコピーできませんでした：{ex.Message}", InfoBarSeverity.Warning);
        }
    }
}
