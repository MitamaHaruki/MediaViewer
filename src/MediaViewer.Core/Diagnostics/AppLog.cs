using System.Text;

namespace MediaViewer.Core.Diagnostics;

/// <summary>
/// 簡易ログ。%LocalAppData%\MediaViewer\logs にプロセスごとのファイルで記録する（多重起動対応、仕様決定事項 §3）。
/// ファイルの中身や個人情報は書かない（仕様 §15）。書き込み失敗でアプリを止めない。
/// </summary>
public static class AppLog
{
    private const int MaxLogFiles = 20;
    private static readonly object Gate = new();
    private static string? s_path;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaViewer", "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                s_path ??= CreateLogFile();
                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(' ').Append(level).Append(' ').Append(message);
                if (ex is not null) sb.AppendLine().Append(ex);
                sb.AppendLine();
                File.AppendAllText(s_path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // ログに失敗しても本来の処理は続ける
        }
    }

    private static string CreateLogFile()
    {
        System.IO.Directory.CreateDirectory(Directory);
        PruneOldLogs();
        return Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Environment.ProcessId}.log");
    }

    private static void PruneOldLogs()
    {
        var files = new DirectoryInfo(Directory).GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxLogFiles - 1);
        foreach (var f in files)
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
