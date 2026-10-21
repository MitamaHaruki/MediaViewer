using System.Diagnostics;
using MediaViewer.Core.Diagnostics;

namespace MediaViewer.App.Services;

/// <summary>
/// 性能測定用の記録（仕様 §11.2）。環境変数 MEDIAVIEWER_TRACE_PERF=1 のときだけ、起動時間・ファイルを開く時間・
/// 描画1回あたりの時間をログへ書く。通常の利用ではログを書かない。ファイル名などの個人情報は記録しない。
/// UIスレッドからだけ呼ぶ。
/// </summary>
internal static class PerfTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MEDIAVIEWER_TRACE_PERF") == "1";

    private const int RenderBatch = 60;
    private static int s_renders;
    private static double s_renderTotal;
    private static double s_renderMax;

    public static void Log(string message)
    {
        if (Enabled) AppLog.Info("[perf] " + message);
    }

    /// <summary>プロセスの開始からの経過時間（ミリ秒）。</summary>
    public static double SinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return (DateTime.Now - process.StartTime).TotalMilliseconds;
    }

    /// <summary>描画1回の時間を集計し、60回ごとに平均と最大を記録する。</summary>
    public static void RecordRender(double milliseconds)
    {
        if (!Enabled) return;
        s_renders++;
        s_renderTotal += milliseconds;
        s_renderMax = Math.Max(s_renderMax, milliseconds);
        if (s_renders >= RenderBatch) FlushRenders();
    }

    public static void FlushRenders()
    {
        if (!Enabled || s_renders == 0) return;
        Log($"描画 {s_renders}回 平均 {s_renderTotal / s_renders:F2} ms 最大 {s_renderMax:F2} ms");
        s_renders = 0;
        s_renderTotal = 0;
        s_renderMax = 0;
    }
}
