using System.Globalization;

namespace MediaViewer.Core.Updates;

/// <summary>
/// アプリから更新プログラム（MediaViewer.Updater.exe）へ渡す指示。両者で同じ定義を使い、書式の食い違いを防ぐ。
/// </summary>
public sealed record UpdaterArguments(
    string InstallDirectory,
    SemanticVersion FromVersion,
    SemanticVersion ToVersion,
    string InstallerPath,
    string SignaturePath,
    string? RollbackInstallerPath,
    string? RollbackSignaturePath,
    int? WaitProcessId,
    bool Relaunch)
{
    public const string Command = "apply";

    /// <summary>Process.Start の ArgumentList に渡す形（引数ごとに分け、引用符の処理を任せる）。</summary>
    public IReadOnlyList<string> ToArgumentList()
    {
        var list = new List<string>
        {
            Command,
            "--install-dir", InstallDirectory,
            "--from", FromVersion.ToString(),
            "--to", ToVersion.ToString(),
            "--msi", InstallerPath,
            "--sig", SignaturePath,
        };
        if (RollbackInstallerPath is not null && RollbackSignaturePath is not null)
        {
            list.AddRange(["--rollback-msi", RollbackInstallerPath, "--rollback-sig", RollbackSignaturePath]);
        }
        if (WaitProcessId is { } pid) list.AddRange(["--wait-pid", pid.ToString(CultureInfo.InvariantCulture)]);
        if (Relaunch) list.Add("--relaunch");
        return list;
    }

    public static UpdaterArguments? TryParse(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count == 0 || args[0] != Command)
        {
            error = "コマンドが指定されていません。";
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var relaunch = false;
        for (var i = 1; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--relaunch")
            {
                relaunch = true;
                continue;
            }
            if (!name.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count || !values.TryAdd(name, args[++i]))
            {
                error = $"引数が不正です：{name}";
                return null;
            }
        }

        string? Get(string name) => values.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
        if (Get("--install-dir") is not { } dir || !Path.IsPathFullyQualified(dir)
            || !SemanticVersion.TryParse(Get("--from"), out var from)
            || !SemanticVersion.TryParse(Get("--to"), out var to)
            || Get("--msi") is not { } msi || !Path.IsPathFullyQualified(msi)
            || Get("--sig") is not { } sig || !Path.IsPathFullyQualified(sig))
        {
            error = "必要な引数が不足しているか、形式が不正です。";
            return null;
        }
        var rollbackMsi = Get("--rollback-msi");
        var rollbackSig = Get("--rollback-sig");
        if ((rollbackMsi is null) != (rollbackSig is null)
            || (rollbackMsi is not null && !Path.IsPathFullyQualified(rollbackMsi))
            || (rollbackSig is not null && !Path.IsPathFullyQualified(rollbackSig)))
        {
            error = "ロールバック用の引数が不正です。";
            return null;
        }
        int? pid = null;
        if (Get("--wait-pid") is { } pidText)
        {
            if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            {
                error = "プロセスIDが不正です。";
                return null;
            }
            pid = p;
        }
        return new UpdaterArguments(dir, from, to, msi, sig, rollbackMsi, rollbackSig, pid, relaunch);
    }
}
