using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Updates;

namespace MediaViewer.Updater;

/// <summary>
/// 更新プログラム。アプリの終了を待ち、ダウンロード済みの MSI を検証して適用し、新しい版の起動を確認する。
/// 起動を確認できなければ、直前の版の MSI で元に戻す（仕様 §10.2-8〜10）。
/// 結果は last-result.json に書き、次にアプリを起動したときに表示する。
/// </summary>
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = UpdaterArguments.TryParse(args, out var error);
        if (parsed is null)
        {
            AppLog.Error($"更新プログラムの引数が不正です：{error}");
            return 2;
        }

        // 同時に2つの更新を走らせない（複数のウィンドウがほぼ同時に終了した場合など）
        using var mutex = new Mutex(initiallyOwned: false, @"Local\MediaViewer.Updater");
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            AppLog.Info("別の更新プログラムが実行中のため終了します。");
            return 3;
        }

        try
        {
            var cache = UpdateCache.CreateDefault();
            UpdateRunResult result;
            try
            {
                result = new UpdateRunner(parsed).Run();
            }
            catch (Exception e)
            {
                AppLog.Error("更新中に想定外のエラーが発生しました。", e);
                result = UpdateRunner.CreateResult(parsed, UpdateRunStatus.Failed, "更新中に想定外のエラーが発生しました。");
            }
            AppLog.Info($"更新の結果：{result.Status} {result.FromVersion} → {result.ToVersion} {result.Message}");
            try
            {
                cache.WriteLastResult(result);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("更新の結果を保存できませんでした。", e);
            }

            if (result.Status == UpdateRunStatus.RollbackFailed)
            {
                // アプリが起動できない可能性があるため、この場で知らせる
                MessageBox(0, result.Message ?? "", "メディアビューワーの更新", MbOk | MbIconError | MbSetForeground);
            }
            if (parsed.Relaunch && result.Status != UpdateRunStatus.RollbackFailed)
            {
                UpdateRunner.Relaunch(parsed.InstallDirectory);
            }
            return result.Status == UpdateRunStatus.Succeeded ? 0 : 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hwnd, string text, string caption, uint type);
}

internal sealed class UpdateRunner(UpdaterArguments args)
{
    private const int ErrorCancelled = 1223;
    private const int MsiSuccess = 0;
    private const int MsiSuccessRebootInitiated = 1641;
    private const int MsiSuccessRebootRequired = 3010;
    private const int MsiUserExit = 1602;
    private const int MsiAnotherInstallInProgress = 1618;

    private static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OtherInstancesTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(90);

    private enum MsiOutcome
    {
        Succeeded,
        Cancelled,
        Failed,
    }

    private string AppPath => Path.Combine(args.InstallDirectory, "MediaViewer.exe");

    public UpdateRunResult Run()
    {
        AppLog.Info($"更新を開始します：{args.FromVersion} → {args.ToVersion}（{args.InstallDirectory}）");
        if (!File.Exists(AppPath))
        {
            return Result(UpdateRunStatus.Failed, "インストール先にアプリが見つからないため、更新しませんでした。");
        }
        if (!WaitForAppExit())
        {
            return Result(UpdateRunStatus.Cancelled, "メディアビューワーのウィンドウが開いていたため、更新を見送りました。すべてのウィンドウを閉じたときに更新します。");
        }

        // 検証してから適用までの間にすり替えられないよう、書き込みを禁止したまま開いておく
        using var installer = OpenLocked(args.InstallerPath);
        if (installer is null || !Verify(installer, args.InstallerPath, args.SignaturePath, args.ToVersion))
        {
            return Result(UpdateRunStatus.Failed, "更新ファイルの検証に失敗したため、更新しませんでした。");
        }
        using var rollback = OpenRollbackPackage();

        var (outcome, message) = RunMsiexec(args.InstallerPath, "update");
        if (outcome == MsiOutcome.Cancelled) return Result(UpdateRunStatus.Cancelled, message);
        if (outcome == MsiOutcome.Failed) return Result(UpdateRunStatus.Failed, message);

        if (HealthCheck(args.ToVersion))
        {
            return Result(UpdateRunStatus.Succeeded, null);
        }

        AppLog.Error($"新しい版（{args.ToVersion}）の起動を確認できませんでした。直前の版に戻します。");
        if (rollback is null)
        {
            return Result(UpdateRunStatus.RollbackFailed,
                "新しい版のメディアビューワーが正常に起動しませんでした。直前の版のファイルがないため、元に戻せませんでした。" +
                "GitHub のリリースページから最新版のインストーラーをダウンロードして、もう一度インストールしてください。");
        }
        var (back, backMessage) = RunMsiexec(args.RollbackInstallerPath!, "rollback");
        if (back == MsiOutcome.Succeeded && HealthCheck(args.FromVersion))
        {
            return Result(UpdateRunStatus.RolledBack, $"新しい版（{args.ToVersion}）が正常に起動しなかったため、直前の版（{args.FromVersion}）に戻しました。");
        }
        return Result(UpdateRunStatus.RollbackFailed,
            "新しい版のメディアビューワーが正常に起動せず、直前の版にも戻せませんでした。" +
            "GitHub のリリースページからインストーラーをダウンロードして、もう一度インストールしてください。" +
            (backMessage is null ? "" : $"（{backMessage}）"));
    }

    public static UpdateRunResult CreateResult(UpdaterArguments args, UpdateRunStatus status, string? message) => new()
    {
        Status = status,
        FromVersion = args.FromVersion.ToString(),
        ToVersion = args.ToVersion.ToString(),
        Message = message,
        FinishedUtc = DateTimeOffset.UtcNow,
    };

    private UpdateRunResult Result(UpdateRunStatus status, string? message) => CreateResult(args, status, message);

    /// <summary>更新を指示したアプリの終了を待ち、ほかのウィンドウも閉じられていることを確かめる。</summary>
    private bool WaitForAppExit()
    {
        if (args.WaitProcessId is { } pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(AppExitTimeout))
                {
                    AppLog.Warn($"アプリ（PID {pid}）が終了しません。");
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // 既に終了している
            }
        }
        var deadline = DateTime.UtcNow + OtherInstancesTimeout;
        while (CountRunningInstances() > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                AppLog.Info("ほかのウィンドウが開いているため、更新を見送ります。");
                return false;
            }
            Thread.Sleep(500);
        }
        return true;
    }

    private int CountRunningInstances()
    {
        var count = 0;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.InstallDirectory));
        foreach (var process in Process.GetProcessesByName("MediaViewer"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), dir, StringComparison.OrdinalIgnoreCase)) count++;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // 他のユーザーのプロセスなど、調べられないものは数えない（ファイル使用中は Windows Installer が扱う）
                }
            }
        }
        return count;
    }

    private static FileStream? OpenLocked(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"更新ファイルを開けません：{Path.GetFileName(path)}", e);
            return null;
        }
    }

    private static bool Verify(FileStream installer, string installerPath, string signaturePath, SemanticVersion version)
    {
        try
        {
            if (new FileInfo(signaturePath).Length > UpdateSignature.MaxSignatureFileBytes) return false;
            var signature = File.ReadAllText(signaturePath);
            var ok = UpdatePackageVerifier.VerifyAsync(installer, Path.GetFileName(installerPath), version, signature,
                UpdateConfiguration.DefaultPublicKeyPem, null, CancellationToken.None).GetAwaiter().GetResult();
            if (!ok) AppLog.Error($"更新ファイルの署名を確認できません：{Path.GetFileName(installerPath)}");
            return ok;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"更新ファイルを検証できません：{Path.GetFileName(installerPath)}", e);
            return false;
        }
    }

    private FileStream? OpenRollbackPackage()
    {
        if (args.RollbackInstallerPath is not { } path || args.RollbackSignaturePath is not { } sig)
        {
            AppLog.Warn("直前の版の MSI がないため、起動確認に失敗しても元に戻せません。");
            return null;
        }
        var stream = OpenLocked(path);
        if (stream is not null && Verify(stream, path, sig, args.FromVersion)) return stream;
        stream?.Dispose();
        AppLog.Warn("直前の版の MSI を検証できないため、ロールバックには使いません。");
        return null;
    }

    /// <summary>
    /// msiexec を管理者権限で実行する（UAC の確認が出る、仕様決定事項 D10）。
    /// 失敗した場合、変更は Windows Installer が巻き戻すため、旧版のまま残る。
    /// </summary>
    private (MsiOutcome Outcome, string? Message) RunMsiexec(string msiPath, string purpose)
    {
        var log = Path.Combine(AppLog.Directory, $"msiexec-{DateTime.Now:yyyyMMdd-HHmmss}-{purpose}.log");
        var installDir = Path.TrimEndingDirectorySeparator(args.InstallDirectory);
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            Arguments = $"/i \"{msiPath}\" /passive /norestart INSTALLFOLDER=\"{installDir}\" /l*v \"{log}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory,
        };
        AppLog.Info($"msiexec を実行します（{purpose}）：{Path.GetFileName(msiPath)}");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (MsiOutcome.Failed, "インストーラーを起動できませんでした。");
            process.WaitForExit();
            var code = process.ExitCode;
            AppLog.Info($"msiexec の終了コード：{code}");
            return code switch
            {
                MsiSuccess or MsiSuccessRebootRequired or MsiSuccessRebootInitiated => (MsiOutcome.Succeeded, null),
                MsiUserExit => (MsiOutcome.Cancelled, "更新が取り消されたため、現在の版のまま使用します。"),
                MsiAnotherInstallInProgress => (MsiOutcome.Cancelled, "ほかのインストールが実行中だったため、更新を見送りました。次に終了したときに更新します。"),
                _ => (MsiOutcome.Failed, $"更新に失敗したため、現在の版のまま使用します（エラーコード {code}）。"),
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            AppLog.Info("管理者権限の確認（UAC）で許可されませんでした。");
            return (MsiOutcome.Cancelled, "管理者権限の確認で許可されなかったため、更新を見送りました。次に終了したときに、もう一度確認します。");
        }
        catch (Win32Exception e)
        {
            AppLog.Error("インストーラーを起動できませんでした。", e);
            return (MsiOutcome.Failed, "インストーラーを起動できませんでした。");
        }
    }

    /// <summary>インストールされた版の番号を確かめ、アプリを起動確認モード（--health-check）で実行する。</summary>
    private bool HealthCheck(SemanticVersion expected)
    {
        try
        {
            var installed = FileVersionInfo.GetVersionInfo(AppPath).ProductVersion;
            if (!SemanticVersion.TryParse(installed, out var version) || version.CompareTo(expected) != 0)
            {
                AppLog.Error($"インストールされた版が想定と異なります：{installed}（想定 {expected}）");
                return false;
            }
            var startInfo = new ProcessStartInfo(AppPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = args.InstallDirectory,
            };
            startInfo.ArgumentList.Add("--health-check");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            if (!process.WaitForExit(HealthCheckTimeout))
            {
                AppLog.Error("起動確認が時間内に終わりませんでした。");
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            AppLog.Info($"起動確認の終了コード：{process.ExitCode}（{expected}）");
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Error("起動確認を実行できませんでした。", e);
            return false;
        }
    }

    public static void Relaunch(string installDirectory)
    {
        try
        {
            var app = Path.Combine(installDirectory, "MediaViewer.exe");
            using var _ = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = installDirectory });
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            AppLog.Warn("アプリを再起動できませんでした。", e);
        }
    }
}
