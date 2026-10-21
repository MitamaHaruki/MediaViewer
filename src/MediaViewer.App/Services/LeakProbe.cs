#if DEBUG
using System.Diagnostics;
using FFmpegInteropX;
using MediaViewer.Core.Diagnostics;
using Windows.Media.Playback;

namespace MediaViewer.App.Services;

/// <summary>
/// 開発用の調査（Debug ビルドのみ）：再生の各部品を繰り返し作って捨て、ハンドルが残るかを調べる。
/// 環境変数 MEDIAVIEWER_LEAK_PROBE=モード、MEDIAVIEWER_LEAK_PROBE_FILE=ファイル で起動すると、結果をログに書いて終了する。
/// </summary>
internal static class LeakProbe
{
    public static string? Mode => Environment.GetEnvironmentVariable("MEDIAVIEWER_LEAK_PROBE");

    public static async Task<int> RunAsync(string mode)
    {
        var path = Environment.GetEnvironmentVariable("MEDIAVIEWER_LEAK_PROBE_FILE") ?? "";
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("MEDIAVIEWER_LEAK_PROBE_COUNT"), out var n) ? n : 60;
        AppLog.Info($"[leak] モード {mode}、{iterations} 回");
        MediaPlayer? shared = mode is "player-reuse" ? NewPlayer() : null;
        if (mode is "window")
        {
            // 実際のメイン画面で同じファイルを繰り返し開く（2台目のモニターに表示）
            var store = new Core.Settings.SettingsStore(Path.Combine(Path.GetTempPath(), $"MediaViewer-leak-{Environment.ProcessId}.json"));
            var window = new MainWindow(store, new Core.Settings.AppSettings { IsMuted = true });
            window.Activate();
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(2100, -1000, 800, 500));
            await Task.Delay(1000);
            Snapshot(0);
            var files = path.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= iterations; i++)
            {
                await window.OpenForProbeAsync(files[i % files.Length]);
                await Task.Delay(400);
                if (i % 30 == 0) Snapshot(i);
            }
            return 0;
        }
        if (mode is "timer")
        {
            // 再生位置の表示に使うタイマーを、ファイルを開くたびと同じように開始・停止する
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.Tick += (_, _) => { };
            Snapshot(0);
            for (var i = 1; i <= 300; i++)
            {
                timer.Start();
                await Task.Delay(20);
                timer.Stop();
                if (i % 100 == 0) Snapshot(i);
            }
            return 0;
        }
        if (mode is "controller")
        {
            // アプリと同じ再生制御クラスを、描画先なしで繰り返し開いて閉じる
            using var device = Imaging.Graphics.GraphicsDevice.Create();
            using var renderer = new Imaging.Rendering.ContentRenderer(device);
            using var controller = new Playback.PlaybackController(renderer, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()) { IsMuted = true };
            Snapshot(0);
            for (var i = 1; i <= iterations; i++)
            {
                await controller.OpenAsync(path, CancellationToken.None);
                await Task.Delay(300);
                controller.Close();
                if (i % 30 == 0) Snapshot(i);
            }
            return 0;
        }
        Snapshot(0);
        for (var i = 1; i <= iterations; i++)
        {
            var config = new MediaSourceConfig();
            var source = await FFmpegMediaSource.CreateFromFileAsync(path, config);
            if (mode is "source")
            {
                source.Dispose();
            }
            else
            {
                var player = shared ?? NewPlayer();
                await source.OpenWithMediaPlayerAsync(player);
                player.Play();
                await Task.Delay(200);
                player.Pause();
                player.Source = null;
                source.Dispose();
                if (shared is null) player.Dispose();
            }
            if (i % 20 == 0) Snapshot(i);
        }
        shared?.Dispose();
        Snapshot(iterations + 1);
        return 0;
    }

    private static MediaPlayer NewPlayer()
    {
        var player = new MediaPlayer { AutoPlay = false, IsVideoFrameServerEnabled = true, IsMuted = true };
        player.CommandManager.IsEnabled = false;
        return player;
    }

    private static void Snapshot(int iteration)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        AppLog.Info($"[leak] {iteration} 回：ハンドル {process.HandleCount}");
    }
}
#endif
