using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaViewer.Core.Diagnostics;

namespace MediaViewer.Core.Conversion;

/// <summary>FFmpeg の実行失敗。Message は利用者向けの日本語、Details は調査用（ログへ記録）。</summary>
public sealed class FfmpegException(string message, string details, int exitCode) : Exception(message)
{
    public string Details { get; } = details;
    public int ExitCode { get; } = exitCode;
}

/// <summary>ffmpeg.exe / ffprobe.exe の場所。</summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath)
{
    /// <summary>
    /// 探す順：アプリと同じ場所の ffmpeg フォルダ → 環境変数 MEDIAVIEWER_FFMPEG_DIR →
    /// （開発時）リポジトリの tools/ffmpeg-dl にダウンロードした FFmpeg。見つからなければ null。
    /// </summary>
    public static FfmpegTools? Locate()
    {
        foreach (var dir in Candidates())
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Environment.GetEnvironmentVariable("MEDIAVIEWER_FFMPEG_DIR") is { Length: > 0 } env) yield return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "tools", "ffmpeg-dl");
            if (!Directory.Exists(tools)) continue;
            foreach (var bin in Directory.EnumerateDirectories(tools, "bin", SearchOption.AllDirectories)) yield return bin;
            yield break;
        }
    }
}

/// <summary>
/// FFmpeg / ffprobe を必要なときだけ起動する（常駐させない、仕様 §11.1）。
/// 引数は ArgumentList で渡し、ファイル名をシェル文字列へ連結しない（仕様 §9.3）。
/// </summary>
public static class FfmpegRunner
{
    private const int StderrTailLines = 30;

    public static async Task<ProbeResult> ProbeAsync(FfmpegTools tools, string inputPath, CancellationToken cancellationToken)
    {
        var args = new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", inputPath };
        var (exit, stdout, stderr) = await RunCapturedAsync(tools.FfprobePath, args, cancellationToken);
        if (exit != 0)
        {
            throw new FfmpegException("ファイルの内容を調べられませんでした。対応していない形式か、ファイルが壊れている可能性があります。", stderr, exit);
        }
        return ProbeResult.Parse(stdout);
    }

    /// <summary>
    /// 変換を実行する。進捗（0〜1）を報告し、キャンセル時は FFmpeg に終了を指示し、応じなければ強制終了する。
    /// </summary>
    public static async Task RunAsync(FfmpegTools tools, IReadOnlyList<string> arguments, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancellationToken, Func<Stream, CancellationToken, Task>? writeInput = null)
    {
        using var process = Start(tools.FfmpegPath, arguments, redirectInput: true);
        // 入力を標準入力から渡す場合（画像の画素データ等）は、書き終えたら閉じる。この場合キャンセルは強制終了で行う
        var inputTask = writeInput is null ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                await writeInput(process.StandardInput.BaseStream, cancellationToken);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }
        }, CancellationToken.None);
        var stderr = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderr)
                {
                    stderr.Enqueue(line);
                    if (stderr.Count > StderrTailLines) stderr.Dequeue();
                }
            }
        }, CancellationToken.None);
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (progress is not null && TryParseProgress(line, duration, out var p)) progress.Report(p);
            }
        }, CancellationToken.None);

        using (cancellationToken.Register(() => { if (writeInput is null) StopGracefully(process); else Kill(process); }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stderrTask, stdoutTask, inputTask);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            string details;
            lock (stderr) details = string.Join(Environment.NewLine, stderr);
            throw new FfmpegException(DescribeFailure(details), details, process.ExitCode);
        }
        progress?.Report(1.0);
    }

    /// <summary>-progress pipe:1 の出力（out_time_us=… / progress=end）から進捗を求める。</summary>
    public static bool TryParseProgress(string line, TimeSpan duration, out double progress)
    {
        progress = 0;
        if (line == "progress=end")
        {
            progress = 1;
            return true;
        }
        if (duration <= TimeSpan.Zero) return false;
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        if (!long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0) return false;
        progress = Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1);
        return true;
    }

    /// <summary>FFmpeg のエラー出力から、利用者向けの日本語の説明を作る。詳細はログへ記録する。</summary>
    private static string DescribeFailure(string details)
    {
        if (details.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "保存先の空き容量が足りません。";
        if (details.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "保存先に書き込む権限がありません。";
        if (details.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) || details.Contains("Encoder not found", StringComparison.OrdinalIgnoreCase))
            return "変換に必要なエンコーダーが FFmpeg に含まれていません。";
        if (details.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)) return "入力ファイルが壊れているか、対応していない形式です。";
        return "変換に失敗しました。";
    }

    private static Process Start(string exe, IEnumerable<string> arguments, bool redirectInput)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg を起動できませんでした。");
        try
        {
            // 変換中も画面操作を軽く保つため、優先度を下げる（仕様 §9.3）
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return process;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCapturedAsync(string exe, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Start(exe, arguments, redirectInput: false);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using (cancellationToken.Register(() => Kill(process)))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>まず FFmpeg に「q」を送って終了させ、数秒で終わらなければプロセスごと強制終了する。</summary>
    private static void StopGracefully(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.StandardInput.Write('q');
            process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Kill(process);
        });
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("FFmpeg の終了に失敗しました", e);
        }
    }
}
