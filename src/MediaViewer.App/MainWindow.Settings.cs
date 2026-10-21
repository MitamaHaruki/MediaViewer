using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaViewer.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MediaViewer.App;

/// <summary>
/// 設定の反映と保存（仕様 §12）、ウィンドウ位置の復元、一般設定画面。
/// 保存は変更した項目だけを最新の設定ファイルへ反映する（多重起動対応）。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>多重起動時に、既存のウィンドウと重ならないようずらす量（物理ピクセル）。</summary>
    private const int CascadeOffset = 32;

    private readonly SettingsStore _settingsStore;
    private readonly List<Task> _pendingSaves = [];
    private DispatcherQueueTimer? _volumeSaveTimer;
    private bool _volumeDirty;
    private bool _applyingSettings;
    private TimeSpan _seekStep = TimeSpan.FromSeconds(5);
    private RectInt32 _normalBounds;
    private bool _wasMaximized;

    /// <summary>起動時に設定を画面へ反映する。</summary>
    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            SetFilter(settings.NavigationFilter);
            VolumeSlider.Value = Math.Round(settings.Volume * 100);
            SetMuted(settings.IsMuted);
            SetRepeat(settings.RepeatEnabled);
            _seekStep = TimeSpan.FromSeconds(settings.SeekStepSeconds);
        }
        finally
        {
            _applyingSettings = false;
        }

        _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        RestoreWindowFromSettings(settings.Window);
        AppWindow.Changed += TrackNormalBounds;
    }

    /// <summary>設定の一部を変更して保存する。ファイル操作でUIを止めないよう別スレッドで行う。</summary>
    private void SaveSettings(Action<AppSettings> change)
    {
        if (_applyingSettings) return;
        var task = Task.Run(() => _settingsStore.Update(change));
        lock (_pendingSaves)
        {
            _pendingSaves.RemoveAll(t => t.IsCompleted);
            _pendingSaves.Add(task);
        }
    }

    /// <summary>終了時、別スレッドで保存中の設定を書き終えるまで少し待つ。</summary>
    private void WaitPendingSaves()
    {
        Task[] pending;
        lock (_pendingSaves) pending = [.. _pendingSaves];
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 保存失敗はログ済み。終了は妨げない
        }
    }

    /// <summary>音量はスライダー操作中に何度も変わるため、操作が落ち着いてから保存する（仕様 §11.1）。</summary>
    private void ScheduleVolumeSave()
    {
        if (_applyingSettings) return;
        _volumeDirty = true;
        if (_volumeSaveTimer is null)
        {
            _volumeSaveTimer = DispatcherQueue.CreateTimer();
            _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(800);
            _volumeSaveTimer.IsRepeating = false;
            _volumeSaveTimer.Tick += (_, _) => SaveVolumeNow();
        }
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void SaveVolumeNow()
    {
        _volumeDirty = false;
        var volume = VolumeSlider.Value / 100.0;
        SaveSettings(s => s.Volume = volume);
    }

    // ---- ウィンドウ位置 ----

    /// <summary>通常表示（最大化・最小化・フルスクリーン以外）のときの位置と大きさを覚えておく。</summary>
    private void TrackNormalBounds(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isFullScreen || sender.Presenter is not OverlappedPresenter p) return;
        if (!(args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)) return;
        if (p.State == OverlappedPresenterState.Restored)
        {
            _normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
        if (p.State != OverlappedPresenterState.Minimized)
        {
            _wasMaximized = p.State == OverlappedPresenterState.Maximized;
        }
    }

    /// <summary>
    /// 前回終了時の位置・大きさ・最大化状態に戻す。モニター構成が変わって画面外になる場合は
    /// 最も近いモニターの作業領域に収め、他のウィンドウと完全に重なる場合はずらす。
    /// </summary>
    private void RestoreWindowFromSettings(WindowPlacement? saved)
    {
        if (saved is null) return;
        var rect = ClampToWorkArea(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height));
        rect = AvoidOtherInstances(rect);
        AppWindow.MoveAndResize(rect);
        _normalBounds = rect;
        if (saved.IsMaximized && AppWindow.Presenter is OverlappedPresenter p)
        {
            p.Maximize();
            _wasMaximized = true;
        }
    }

    private static RectInt32 ClampToWorkArea(RectInt32 rect)
    {
        var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
        var w = Math.Min(rect.Width, area.Width);
        var h = Math.Min(rect.Height, area.Height);
        var x = Math.Clamp(rect.X, area.X, area.X + area.Width - w);
        var y = Math.Clamp(rect.Y, area.Y, area.Y + area.Height - h);
        return new RectInt32(x, y, w, h);
    }

    /// <summary>同じ位置に既に MediaViewer のウィンドウがあれば、斜めにずらして開く（多重起動）。</summary>
    private RectInt32 AvoidOtherInstances(RectInt32 rect)
    {
        var others = new List<(int X, int Y)>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (GetWindowRect(p.MainWindowHandle, out var r)) others.Add((r.Left, r.Top));
                }
            }
        }
        catch (Exception)
        {
            return rect;
        }

        for (var i = 0; i < 10 && others.Any(o => Math.Abs(o.X - rect.X) < 8 && Math.Abs(o.Y - rect.Y) < 8); i++)
        {
            rect = ClampToWorkArea(new RectInt32(rect.X + CascadeOffset, rect.Y + CascadeOffset, rect.Width, rect.Height));
        }
        return rect;
    }

    /// <summary>終了時のウィンドウ状態を保存する。フルスクリーン中なら、その前の状態を保存する。</summary>
    private void SaveWindowPlacementOnClose()
    {
        var bounds = _isFullScreen ? _restoreRect : _normalBounds;
        var maximized = _isFullScreen ? _restoreState == OverlappedPresenterState.Maximized : _wasMaximized;
        // 音量は、このウィンドウで変更してまだ保存していない場合だけ書く（他のウィンドウの変更を戻さない）
        double? volume = _volumeDirty ? VolumeSlider.Value / 100.0 : null;
        WaitPendingSaves();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        // 終了処理の中なので同期で保存する（ファイルは小さく、待ち時間は最大でも数秒）
        _settingsStore.Update(s =>
        {
            s.Window = new WindowPlacement
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = maximized,
            };
            if (volume is { } v) s.Volume = v;
        });
    }

    // ---- 一般設定 ----

    private async void OnGeneralSettingsClick(object sender, RoutedEventArgs e)
    {
        var current = _settingsStore.Load();
        var seek = new ComboBox
        {
            Header = "シーク幅（← → キー）",
            MinWidth = 200,
            ItemsSource = AppSettings.SeekStepChoices.Select(v => $"{v} 秒").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.SeekStepChoices, current.SeekStepSeconds)),
        };
        var openFolder = new HyperlinkButton { Content = "設定ファイルの場所を開く", Padding = new Thickness(0) };
        openFolder.Click += (_, _) => OpenSettingsFolder();

        var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
        panel.Children.Add(seek);
        panel.Children.Add(new TextBlock
        {
            Text = "音量・ミュート・前後移動の対象・ウィンドウの位置は自動で保存されます。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        panel.Children.Add(openFolder);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "一般設定",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var seconds = AppSettings.SeekStepChoices[Math.Max(0, seek.SelectedIndex)];
        _seekStep = TimeSpan.FromSeconds(seconds);
        SaveSettings(s => s.SeekStepSeconds = seconds);
    }

    private void OpenSettingsFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsStore.FilePath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ShowMessage($"設定ファイルの場所を開けませんでした：{ex.Message}", InfoBarSeverity.Warning);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
