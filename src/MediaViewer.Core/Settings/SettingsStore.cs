using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaViewer.Core.Diagnostics;

namespace MediaViewer.Core.Settings;

/// <summary>
/// 設定ファイル（JSON）の読み書き。多重起動（仕様決定事項 D5）を前提に、
/// <list type="bullet">
/// <item>保存はプロセス間の排他（名前付きミューテックス）の中で、ディスク上の最新内容を読み直し、変更分だけを反映して書く。</item>
/// <item>一時ファイルへ書いてから置き換え、書き込み途中で落ちても壊れたファイルを残さない。</item>
/// <item>壊れたファイルは退避して既定値で復旧する（仕様 §12）。</item>
/// </list>
/// 設定ファイルはインストール先と分けて %LocalAppData% に置き、更新・ロールバックで消えないようにする（仕様 §10.3）。
/// </summary>
public sealed class SettingsStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public string FilePath { get; }

    private readonly string _mutexName;

    public SettingsStore(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())))[..16];
        _mutexName = $@"Local\MediaViewer.Settings.{hash}";
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaViewer", "settings.json");

    /// <summary>
    /// 通常の設定ファイルを使う。環境変数 MEDIAVIEWER_SETTINGS_PATH があればそのパスを使う
    /// （自動テストで利用者の設定を書き換えないため）。
    /// </summary>
    public static SettingsStore CreateDefault() =>
        new(Environment.GetEnvironmentVariable("MEDIAVIEWER_SETTINGS_PATH") is { Length: > 0 } overridePath ? overridePath : DefaultFilePath);

    /// <summary>設定を読む。ファイルがない・壊れている場合は既定値を返す（壊れたファイルは退避する）。</summary>
    public AppSettings Load()
    {
        try
        {
            return WithLock(ReadOrDefault);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            AppLog.Warn("設定を読み込めないため既定値を使います", e);
            return new AppSettings();
        }
    }

    /// <summary>
    /// 設定を変更して保存する。他のプロセスが保存した内容を失わないよう、最新の内容に change だけを適用する。
    /// 保存できなくてもアプリは続行する（戻り値 false）。
    /// </summary>
    public bool Update(Action<AppSettings> change)
    {
        try
        {
            WithLock(() =>
            {
                var current = ReadOrDefault();
                change(current);
                current.Normalize();
                current.SchemaVersion = Math.Max(current.SchemaVersion, AppSettings.CurrentSchemaVersion);
                Write(current);
                return true;
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            AppLog.Warn("設定を保存できませんでした", e);
            return false;
        }
    }

    private AppSettings ReadOrDefault()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var json = File.ReadAllBytes(FilePath);
            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException e)
        {
            // 壊れた設定は調査できるよう退避し、既定値で続行する
            var backup = $"{FilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(FilePath, backup, overwrite: true); } catch (IOException) { }
            AppLog.Warn($"設定ファイルが壊れているため既定値で復旧しました（退避先：{Path.GetFileName(backup)}）", e);
            return new AppSettings();
        }
    }

    private void Write(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = $"{FilePath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, settings, SettingsJsonContext.Default.AppSettings);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // 他のプロセスが保持したまま終了した。ファイルは一時ファイル経由で書くため中身は壊れていない
            owned = true;
        }
        if (!owned) throw new TimeoutException("設定ファイルの排他を取得できませんでした。");
        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
