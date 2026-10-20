using MediaViewer.App.Services;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Settings;
using MediaViewer.Core.Updates;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MediaViewer.App;

/// <summary>
/// 自動更新の画面側（仕様 §10.2）：起動後の確認、更新の通知、更新設定、終了時・今すぐの適用。
/// 確認とダウンロードは起動直後の表示が落ち着いてから、バックグラウンドで行う。
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(5);

    private AppUpdateService? _updates;
    private bool _updateBusy;
    private bool _closeConfirmed;
    private bool _askingUpdateOnClose;
    private ProgressBar? _updateProgress;

    private void InitializeUpdates()
    {
        _updates = AppUpdateService.TryCreate(_settingsStore);
        MenuCheckUpdates.IsEnabled = _updates is not null;
        MenuUpdateSettings.IsEnabled = _updates is not null;
        AppWindow.Closing += OnAppWindowClosing;
        if (_updates is null) return;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = StartupCheckDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _ = RunStartupUpdateTasksAsync();
        timer.Start();
    }

    private async Task RunStartupUpdateTasksAsync()
    {
        var updates = _updates!;
        try
        {
            var (last, pending) = await Task.Run(() =>
            {
                var result = updates.Cache.TakeLastResult();
                updates.Cleanup();
                return (result, updates.FindPending());
            });
            if (last is not null) ShowUpdateRunResult(last);
            if (pending is not null)
            {
                ShowPendingUpdate(pending);
                return;
            }
            var settings = await Task.Run(_settingsStore.Load);
            if (settings.Update.AutoCheck && UpdateSchedule.IsCheckDue(settings.Update.LastCheckedUtc, DateTimeOffset.UtcNow))
            {
                await CheckForUpdatesAsync(userInitiated: false);
            }
        }
        catch (Exception e)
        {
            // 更新の不具合で通常の機能を妨げない（仕様 §10.3）
            AppLog.Error("起動時の更新処理に失敗しました。", e);
        }
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(userInitiated: true);

    /// <summary>
    /// 更新を確認する。自動確認では、新しい版の準備ができるまで何も表示しない（再生や変換を妨げない）。
    /// 利用者が「更新を確認」を選んだ場合は、確認中・ダウンロード中の進み具合と結果を表示する。
    /// </summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updates is not { } updates || _updateBusy) return;
        _updateBusy = true;
        MenuCheckUpdates.IsEnabled = false;
        try
        {
            if (userInitiated && updates.FindPending() is { } ready)
            {
                ShowPendingUpdate(ready);
                return;
            }
            var progress = userInitiated ? new Progress<double>(ReportUpdateProgress) : null;
            if (userInitiated) ShowUpdateBar("更新を確認しています...", "", InfoBarSeverity.Informational, progress: -1);
            var result = await Task.Run(() => updates.CheckAsync(progress, forceDownload: false, CancellationToken.None));

            if (result.Status == UpdateCheckStatus.UpToDate)
            {
                if (userInitiated)
                {
                    ShowUpdateBar("お使いのバージョンは最新です", $"バージョン {updates.CurrentVersion}", InfoBarSeverity.Success);
                }
                return;
            }
            if (updates.FindPending() is { } pending)
            {
                ShowPendingUpdate(pending);
            }
            else if (result.Release is { } release)
            {
                ShowAvailableUpdate(release);
            }
        }
        catch (UpdateException ex)
        {
            AppLog.Warn($"更新を確認できませんでした：{ex.Message} {ex.Details}", ex.InnerException);
            if (userInitiated) ShowUpdateBar("更新を確認できませんでした", ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("更新の確認中に想定外のエラーが発生しました。", ex);
            if (userInitiated) ShowUpdateBar("更新を確認できませんでした", "想定外のエラーが発生しました。詳細はログを確認してください。", InfoBarSeverity.Warning);
        }
        finally
        {
            _updateBusy = false;
            MenuCheckUpdates.IsEnabled = true;
        }
    }

    /// <summary>新しい版があるが、まだダウンロードしていない（自動ダウンロードが無効など）。</summary>
    private void ShowAvailableUpdate(UpdateRelease release)
    {
        ShowUpdateBar($"新しいバージョン {release.Version} があります", "",
            InfoBarSeverity.Informational, null,
            ("ダウンロード", () => _ = DownloadUpdateAsync(release)),
            ("変更内容", () => _ = ShowReleaseNotesAsync(release.Title, release.Notes, release.ReleasePage?.ToString())));
    }

    private async Task DownloadUpdateAsync(UpdateRelease release)
    {
        if (_updates is not { } updates || _updateBusy) return;
        _updateBusy = true;
        try
        {
            ShowUpdateBar($"バージョン {release.Version} をダウンロードしています...", "", InfoBarSeverity.Informational, progress: 0);
            var progress = new Progress<double>(ReportUpdateProgress);
            await Task.Run(() => updates.DownloadAsync(release, progress, CancellationToken.None));
            if (updates.FindPending() is { } pending)
            {
                ShowPendingUpdate(pending);
            }
            else
            {
                ShowUpdateBar("別のウィンドウでダウンロードしています", "完了すると、終了時に更新できます。", InfoBarSeverity.Informational);
            }
        }
        catch (UpdateException ex)
        {
            AppLog.Warn($"更新ファイルをダウンロードできませんでした：{ex.Message} {ex.Details}", ex.InnerException);
            ShowUpdateBar("更新ファイルをダウンロードできませんでした", ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("更新ファイルのダウンロード中に想定外のエラーが発生しました。", ex);
            ShowUpdateBar("更新ファイルをダウンロードできませんでした", "想定外のエラーが発生しました。詳細はログを確認してください。", InfoBarSeverity.Warning);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    /// <summary>ダウンロード済みの新しい版がある。既定では終了時に適用し、「今すぐ再起動して更新」も選べる（仕様 §10.2-7）。</summary>
    private void ShowPendingUpdate(DownloadedUpdate pending)
    {
        var notes = pending.Info;
        var showNotes = ("変更内容", (Action)(() => _ = ShowReleaseNotesAsync(notes?.Title ?? $"バージョン {pending.Version}", notes?.Notes ?? "", notes?.ReleasePageUrl)));
        if (!AppUpdateService.CanApply)
        {
            ShowUpdateBar($"新しいバージョン {pending.Version} があります",
                "この環境（開発用のビルドなど）では自動で更新できません。リリースページからインストーラーを入手してください。",
                InfoBarSeverity.Informational, null, showNotes);
            return;
        }
        var mode = _settingsStore.Load().Update.ApplyMode;
        var message = mode == UpdateApplyMode.OnExit
            ? "アプリを終了するときに更新します。"
            : "アプリを終了するときに、更新するかどうかを確認します。";
        ShowUpdateBar($"新しいバージョン {pending.Version} の準備ができました", message, InfoBarSeverity.Success, null,
            ("今すぐ再起動して更新", () => _ = UpdateNowAsync()),
            showNotes);
    }

    private void ShowUpdateRunResult(UpdateRunResult result)
    {
        switch (result.Status)
        {
            case UpdateRunStatus.Succeeded:
                ShowUpdateBar($"バージョン {result.ToVersion} に更新しました", "", InfoBarSeverity.Success);
                break;
            case UpdateRunStatus.Cancelled:
                ShowUpdateBar("更新を見送りました", result.Message ?? "", InfoBarSeverity.Informational);
                break;
            case UpdateRunStatus.Failed:
                ShowUpdateBar("更新できませんでした", result.Message ?? "", InfoBarSeverity.Warning);
                break;
            case UpdateRunStatus.RolledBack:
                ShowUpdateBar("直前のバージョンに戻しました", result.Message ?? "", InfoBarSeverity.Warning);
                break;
            default:
                ShowUpdateBar("更新に失敗しました", result.Message ?? "", InfoBarSeverity.Error);
                break;
        }
    }

    /// <summary>「今すぐ再起動して更新」。変換中は中断させず、他のウィンドウがあれば閉じてもらう。</summary>
    private async Task UpdateNowAsync()
    {
        if (_updates is not { } updates || updates.FindPending() is not { } pending) return;
        if (_conversionWindow?.IsConverting == true)
        {
            ShowUpdateBar("変換中のため更新できません", "変換が終わってから、もう一度お試しください。", InfoBarSeverity.Warning);
            return;
        }
        if (AppUpdateService.OtherInstancesRunning())
        {
            ShowUpdateBar("ほかのウィンドウが開いています", "ほかのメディアビューワーのウィンドウを閉じてから、もう一度お試しください。", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"バージョン {pending.Version} に更新します",
            Content = "メディアビューワーを閉じて更新し、更新後にもう一度起動します。\n更新の途中で、管理者権限の確認（ユーザーアカウント制御）が表示されます。",
            PrimaryButtonText = "再起動して更新",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!updates.TryStartUpdater(pending, relaunch: true))
        {
            ShowUpdateBar("更新を開始できませんでした", "詳細はログを確認してください。", InfoBarSeverity.Warning);
            return;
        }
        _closeConfirmed = true;
        _conversionWindow?.Close();
        Close();
    }

    // ---- 終了時の更新 ----

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        if (_askingUpdateOnClose || !HandleUpdateOnClose())
        {
            args.Cancel = true;
            return;
        }
        _closeConfirmed = true;
    }

    /// <summary>メニューの「終了」。閉じるボタンと同じく、終了時の更新を扱う。</summary>
    private void RequestClose()
    {
        if (_askingUpdateOnClose || !HandleUpdateOnClose()) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>
    /// 終了時に適用する更新があれば扱う。確認の画面を出した場合は false（終了を保留）。
    /// このアプリの最後のウィンドウのときだけ適用し、Windows のシャットダウン中は何もしない。
    /// </summary>
    private bool HandleUpdateOnClose()
    {
        try
        {
            if (_updates is not { } updates || !AppUpdateService.CanApply || AppUpdateService.IsSessionEnding) return true;
            if (_conversionWindow is not null) return true; // 変換画面が残る（プロセスが続く）ため、今回は適用しない
            if (updates.FindPending() is not { } pending || AppUpdateService.OtherInstancesRunning()) return true;

            if (_settingsStore.Load().Update.ApplyMode == UpdateApplyMode.OnExit)
            {
                updates.TryStartUpdater(pending, relaunch: false);
                return true;
            }
            _ = AskUpdateOnCloseAsync(updates, pending);
            return false;
        }
        catch (Exception e)
        {
            AppLog.Error("終了時の更新の準備に失敗しました。", e);
            return true;
        }
    }

    private async Task AskUpdateOnCloseAsync(AppUpdateService updates, DownloadedUpdate pending)
    {
        _askingUpdateOnClose = true;
        ContentDialogResult result;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"バージョン {pending.Version} に更新しますか？",
                Content = "更新してから終了します。更新の途中で、管理者権限の確認（ユーザーアカウント制御）が表示されます。\n更新しない場合は、次に終了するときにもう一度確認します。",
                PrimaryButtonText = "更新して終了",
                SecondaryButtonText = "更新せずに終了",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark,
            };
            result = await dialog.ShowAsync();
        }
        finally
        {
            _askingUpdateOnClose = false;
        }
        if (result == ContentDialogResult.None) return;
        if (result == ContentDialogResult.Primary) updates.TryStartUpdater(pending, relaunch: false);
        _closeConfirmed = true;
        Close();
    }

    // ---- 更新設定 ----

    private async void OnUpdateSettingsClick(object sender, RoutedEventArgs e)
    {
        var current = _settingsStore.Load().Update;
        var autoCheck = new CheckBox { Content = "起動時に更新を自動で確認する", IsChecked = current.AutoCheck };
        var autoDownload = new CheckBox { Content = "新しいバージョンを自動でダウンロードする", IsChecked = current.AutoDownload };
        var applyMode = new RadioButtons
        {
            Header = "更新の適用",
            ItemsSource = new List<string> { "アプリの終了時に自動で更新する（推奨）", "アプリの終了時に、更新するか確認する" },
            SelectedIndex = current.ApplyMode == UpdateApplyMode.AskEachTime ? 1 : 0,
        };
        var lastChecked = current.LastCheckedUtc is { } t ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "未確認";
        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(autoCheck);
        panel.Children.Add(autoDownload);
        panel.Children.Add(applyMode);
        panel.Children.Add(new TextBlock
        {
            Text = $"現在のバージョン：{_updates?.CurrentVersion}\n最後に確認した日時：{lastChecked}\n\n" +
                   "更新は GitHub Releases から取得し、署名を確認してから適用します。" +
                   "メディアビューワーは Program Files にインストールされるため、更新の適用時に管理者権限の確認（ユーザーアカウント制御）が表示されます。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "更新設定",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var check = autoCheck.IsChecked == true;
        var download = autoDownload.IsChecked == true;
        var mode = applyMode.SelectedIndex == 1 ? UpdateApplyMode.AskEachTime : UpdateApplyMode.OnExit;
        SaveSettings(s =>
        {
            s.Update.AutoCheck = check;
            s.Update.AutoDownload = download;
            s.Update.ApplyMode = mode;
        });
    }

    // ---- 表示 ----

    private async Task ShowReleaseNotesAsync(string title, string notes, string? releasePageUrl)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(notes) ? "（変更内容の記載はありません）" : notes,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });
        if (Uri.TryCreate(releasePageUrl, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps
            && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(new HyperlinkButton { Content = "リリースページを開く", NavigateUri = page, Padding = new Thickness(0) });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"{title} の変更内容",
            Content = panel,
            CloseButtonText = "閉じる",
            RequestedTheme = ElementTheme.Dark,
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // 別のダイアログを表示中
            AppLog.Warn("変更内容を表示できませんでした。", e);
        }
    }

    /// <summary>更新の通知を表示する。progress は 0〜1、負の値は進み具合不明、null は進み具合なし。</summary>
    private void ShowUpdateBar(string title, string message, InfoBarSeverity severity, double? progress = null, params (string Text, Action Click)[] actions)
    {
        UpdateBar.Title = title;
        UpdateBar.Message = message;
        UpdateBar.Severity = severity;
        _updateProgress = null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        if (progress is { } p)
        {
            _updateProgress = new ProgressBar { Width = 240, Maximum = 1, Value = Math.Max(0, p), IsIndeterminate = p < 0, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(_updateProgress);
        }
        foreach (var (text, click) in actions)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => click();
            panel.Children.Add(button);
        }
        UpdateBar.Content = panel.Children.Count > 0 ? panel : null;
        UpdateBar.IsOpen = true;
    }

    private void ReportUpdateProgress(double value)
    {
        if (_updateProgress is not { } bar) return;
        bar.IsIndeterminate = false;
        bar.Value = value;
        if (value > 0 && value < 1) UpdateBar.Title = $"更新ファイルをダウンロードしています... {value:P0}";
    }

    private void OnUpdateBarClosed(InfoBar sender, InfoBarClosedEventArgs args) => _updateProgress = null;
}
