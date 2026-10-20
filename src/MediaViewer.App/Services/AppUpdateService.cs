using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Settings;
using MediaViewer.Core.Updates;

namespace MediaViewer.App.Services;

/// <summary>
/// アプリ側の自動更新（仕様 §10.2）。確認・ダウンロードはバックグラウンドで行い、表示や再生を妨げない。
/// 適用は別プロセスの更新プログラム（MediaViewer.Updater.exe）に任せる。
/// </summary>
internal sealed partial class AppUpdateService
{
    private const string UpdaterFileName = "MediaViewer.Updater.exe";

    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppUpdateService(UpdateConfiguration config, UpdateCache cache, SettingsStore settingsStore)
    {
        Config = config;
        Cache = cache;
        _settingsStore = settingsStore;
    }

    public UpdateConfiguration Config { get; }
    public UpdateCache Cache { get; }

    public SemanticVersion CurrentVersion => Config.CurrentVersion;

    /// <summary>更新の配信元が設定されていなければ null（更新機能なし）。</summary>
    public static AppUpdateService? TryCreate(SettingsStore settingsStore)
    {
        var config = UpdateConfiguration.FromAssembly(typeof(AppUpdateService).Assembly);
        return config is null ? null : new AppUpdateService(config, UpdateCache.CreateDefault(), settingsStore);
    }

    private static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>インストーラーで導入された環境（更新プログラムが同梱されている）なら更新を適用できる。</summary>
    public static bool CanApply => File.Exists(Path.Combine(InstallDirectory, UpdaterFileName));

    /// <summary>最新版を確認し、新しい版があれば（設定に従い）ダウンロードまで行う。</summary>
    public async Task<UpdateCheckResult> CheckAsync(IProgress<double>? downloadProgress, bool forceDownload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var client = new UpdateClient(Config, Cache);
            var result = await client.CheckAsync(cancellationToken);
            await Task.Run(() => _settingsStore.Update(s => s.Update.LastCheckedUtc = DateTimeOffset.UtcNow), CancellationToken.None);
            AppLog.Info($"更新を確認しました：現在 {CurrentVersion}、最新 {result.Latest?.ToString() ?? "なし"}");
            if (result.Release is { } release && (forceDownload || _settingsStore.Load().Update.AutoDownload))
            {
                await DownloadCoreAsync(client, release, downloadProgress, cancellationToken);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>確認済みの新しい版をダウンロードする（自動ダウンロードが無効なときに利用者が選んだ場合）。</summary>
    public async Task DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var client = new UpdateClient(Config, Cache);
            await DownloadCoreAsync(client, release, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadCoreAsync(UpdateClient client, UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // 複数のウィンドウで同時にダウンロードしない。他のウィンドウが取得中なら任せる
        using var downloadLock = Cache.TryAcquireDownloadLock();
        if (downloadLock is null)
        {
            AppLog.Info("他のウィンドウが更新ファイルをダウンロード中のため、そちらに任せます。");
            return;
        }
        await client.DownloadAsync(release, progress, cancellationToken);

        // 起動確認に失敗したとき戻せるよう、現在の版の MSI も手元に置く（初回のみ、仕様 §10.2-10）
        if (CanApply && Cache.Find(Config, CurrentVersion) is null && !CurrentVersion.IsPreRelease)
        {
            try
            {
                if (await client.GetReleaseAsync(CurrentVersion, cancellationToken) is { } current)
                {
                    await client.DownloadAsync(current, null, cancellationToken);
                }
            }
            catch (UpdateException e)
            {
                AppLog.Warn($"ロールバック用に現在の版の MSI を取得できませんでした：{e.Message} {e.Details}");
            }
        }
    }

    /// <summary>ダウンロード済みで、まだ適用していない新しい版（ファイルの有無だけを見る。検証は更新プログラムが行う）。</summary>
    public DownloadedUpdate? FindPending()
    {
        try
        {
            return Cache.FindPending(Config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("ダウンロード済みの更新を確認できませんでした。", e);
            return null;
        }
    }

    public void Cleanup()
    {
        try
        {
            Cache.Cleanup(CurrentVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("更新ファイルの整理に失敗しました。", e);
        }
    }

    /// <summary>このアプリの他のプロセス（別のウィンドウ）が動いているか。</summary>
    public static bool OtherInstancesRunning()
    {
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("MediaViewer"))
        {
            using (process)
            {
                if (process.Id == self) continue;
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && string.Equals(Path.GetDirectoryName(path), InstallDirectory, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // 調べられないプロセス（他のユーザーのものなど）は数えない
                }
            }
        }
        return false;
    }

    /// <summary>Windows のシャットダウン・サインアウト中か（その場合は更新を始めない）。</summary>
    public static bool IsSessionEnding => GetSystemMetrics(SmShuttingDown) != 0;

    /// <summary>
    /// 更新プログラムを起動する。インストール先の更新プログラムは更新で置き換わるため、%LocalAppData% へ複写して実行する。
    /// 更新プログラムはこのプロセスの終了を待ってから適用する。
    /// </summary>
    public bool TryStartUpdater(DownloadedUpdate update, bool relaunch)
    {
        if (!CanApply) return false;
        try
        {
            var source = Path.Combine(InstallDirectory, UpdaterFileName);
            var dir = Path.Combine(Path.GetDirectoryName(Cache.Root)!, "updater");
            Directory.CreateDirectory(dir);
            var updater = Path.Combine(dir, UpdaterFileName);
            File.Copy(source, updater, overwrite: true);

            var rollback = Cache.Find(Config, CurrentVersion);
            var args = new UpdaterArguments(InstallDirectory, CurrentVersion, update.Version, update.InstallerPath, update.SignaturePath,
                rollback?.InstallerPath, rollback?.SignaturePath, Environment.ProcessId, relaunch);
            var startInfo = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var arg in args.ToArgumentList()) startInfo.ArgumentList.Add(arg);
            using var process = Process.Start(startInfo);
            AppLog.Info($"更新プログラムを起動しました：{CurrentVersion} → {update.Version}（再起動 {relaunch}）");
            return process is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            AppLog.Error("更新プログラムを起動できませんでした。", e);
            return false;
        }
    }

    private const int SmShuttingDown = 0x2000;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
