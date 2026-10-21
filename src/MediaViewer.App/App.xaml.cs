using MediaViewer.App.Services;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Settings;
using Microsoft.UI.Xaml;

namespace MediaViewer.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // 想定外の例外でアプリ全体を落とさない（仕様 §15）。詳細ログは今後のフェーズで記録先を整備する
        UnhandledException += (_, e) =>
        {
            AppLog.Error("未処理の例外", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未観測のタスク例外", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).Contains(HealthCheck.Argument))
        {
            Environment.Exit(HealthCheck.Run());
            return;
        }

#if DEBUG
        if (LeakProbe.Mode is { Length: > 0 } probe)
        {
            _ = RunLeakProbeAsync(probe);
            return;
        }
#endif

        var store = SettingsStore.CreateDefault();
        _window = new MainWindow(store, store.Load());
        _window.Activate();
        PerfTrace.Log($"起動からウィンドウ表示まで {PerfTrace.SinceProcessStart():F0} ms");

        // 関連付け起動・起動引数のローカルファイルパス（仕様 §3.2）。多重起動は許可（仕様決定事項 D5）
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        if (path is not null)
        {
            _window.OpenFromStartup(Path.GetFullPath(path));
        }
    }
#if DEBUG
    private static async Task RunLeakProbeAsync(string mode)
    {
        try
        {
            Environment.Exit(await LeakProbe.RunAsync(mode));
        }
        catch (Exception e)
        {
            AppLog.Error("[leak] 失敗", e);
            Environment.Exit(1);
        }
    }
#endif
}
