using System.Diagnostics;
using MediaViewer.Core.Conversion;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Settings;
using MediaViewer.Imaging.Graphics;

namespace MediaViewer.App.Services;

/// <summary>
/// 起動確認（--health-check）。更新プログラムが、更新後の版が正常に動くかを確かめるために使う（仕様 §10.2-9）。
/// 更新で壊れうるもの（画面の定義、同梱ライブラリ、FFmpeg）を読み込み、ウィンドウは表示しない。
/// 利用者の設定ファイルには書き込まない。
/// </summary>
internal static class HealthCheck
{
    public const string Argument = "--health-check";

    public static int Run()
    {
        AppLog.Info("起動確認を開始します。");
        var ok = true;
        ok &= Check("画面", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), $"MediaViewer-health-check-{Environment.ProcessId}.json");
            try
            {
                var store = new SettingsStore(temp);
                var window = new MainWindow(store, new AppSettings());
                window.Close();
            }
            finally
            {
                File.Delete(temp);
            }
        });
        ok &= Check("描画", () => GraphicsDevice.Create().Dispose());
        ok &= Check("再生", () => _ = new FFmpegInteropX.MediaSourceConfig());
        ok &= Check("変換", () =>
        {
            var tools = FfmpegTools.Locate() ?? throw new FileNotFoundException("FFmpeg が見つかりません。");
            foreach (var exe in new[] { tools.FfmpegPath, tools.FfprobePath })
            {
                using var process = Process.Start(new ProcessStartInfo(exe, "-hide_banner -version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                }) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} を起動できません。");
                _ = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(TimeSpan.FromSeconds(20)) || process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"{Path.GetFileName(exe)} が正常に終了しません。");
                }
            }
        });
        AppLog.Info(ok ? "起動確認：正常" : "起動確認：異常あり");
        return ok ? 0 : 1;
    }

    private static bool Check(string name, Action action)
    {
        try
        {
            action();
            AppLog.Info($"起動確認 {name}：OK");
            return true;
        }
        catch (Exception e)
        {
            AppLog.Error($"起動確認 {name}：失敗", e);
            return false;
        }
    }
}
