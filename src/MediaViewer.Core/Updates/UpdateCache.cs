using System.Security.Cryptography;
using System.Text.Json;
using MediaViewer.Core.Diagnostics;

namespace MediaViewer.Core.Updates;

/// <summary>ダウンロード済みの更新ファイル一組（MSI と署名）。</summary>
public sealed record DownloadedUpdate(SemanticVersion Version, string InstallerPath, string SignaturePath, DownloadedUpdateInfo? Info);

/// <summary>
/// 更新ファイルの保存場所（%LocalAppData%\MediaViewer\updates\{バージョン}\）。インストール先や設定とは分ける（仕様 §10.3）。
/// 新しい版のファイルに加え、ロールバック用に現在の版と直前の版のファイルを残す（仕様 §10.2-10）。
/// </summary>
public sealed class UpdateCache(string root)
{
    private const string InfoFileName = "update.json";
    private const string LastResultFileName = "last-result.json";

    public string Root { get; } = root;

    /// <summary>既定の保存場所。テストでは環境変数 MEDIAVIEWER_UPDATES_DIR で差し替える。</summary>
    public static UpdateCache CreateDefault() => new(
        Environment.GetEnvironmentVariable("MEDIAVIEWER_UPDATES_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaViewer", "updates"));

    public string DirectoryFor(SemanticVersion version) => Path.Combine(Root, version.ToString());

    public string InstallerPath(UpdateConfiguration config, SemanticVersion version) =>
        Path.Combine(DirectoryFor(version), config.InstallerAssetName(version));

    public string SignaturePath(UpdateConfiguration config, SemanticVersion version) =>
        Path.Combine(DirectoryFor(version), config.SignatureAssetName(version));

    /// <summary>指定の版のファイルが揃っていれば返す（内容の検証はしない）。</summary>
    public DownloadedUpdate? Find(UpdateConfiguration config, SemanticVersion version)
    {
        var installer = InstallerPath(config, version);
        var signature = SignaturePath(config, version);
        if (!File.Exists(installer) || !File.Exists(signature)) return null;
        return new DownloadedUpdate(version, installer, signature, ReadInfo(version));
    }

    /// <summary>現在の版より新しい、ダウンロード済みの更新のうち最新のもの（内容の検証はしない）。</summary>
    public DownloadedUpdate? FindPending(UpdateConfiguration config) =>
        EnumerateVersions()
            .Where(v => v > config.CurrentVersion && !v.IsPreRelease)
            .OrderDescending()
            .Select(v => Find(config, v))
            .FirstOrDefault(u => u?.Info is not null);

    public void WriteInfo(SemanticVersion version, DownloadedUpdateInfo info)
    {
        var path = Path.Combine(DirectoryFor(version), InfoFileName);
        WriteJsonAtomically(path, JsonSerializer.SerializeToUtf8Bytes(info, UpdateJsonContext.Default.DownloadedUpdateInfo));
    }

    private DownloadedUpdateInfo? ReadInfo(SemanticVersion version)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(DirectoryFor(version), InfoFileName));
            return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.DownloadedUpdateInfo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 不要になった更新ファイルを消す。残すのは、現在の版（次の更新時のロールバック用）、それより新しい版、
    /// 現在の版の直前の版（更新直後の一定期間、戻せるように）。
    /// </summary>
    public void Cleanup(SemanticVersion current)
    {
        var versions = EnumerateVersions().ToList();
        var previous = versions.Where(v => v < current).OrderDescending().FirstOrDefault();
        foreach (var version in versions)
        {
            var dir = DirectoryFor(version);
            if (version >= current || version == previous)
            {
                foreach (var partial in SafeEnumerateFiles(dir, "*.partial")) TryDelete(partial);
                continue;
            }
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"古い更新ファイルを削除できませんでした：{version}", e);
            }
        }
    }

    private IEnumerable<SemanticVersion> EnumerateVersions()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (SemanticVersion.TryParse(Path.GetFileName(dir), out var v) && v.ToString() == Path.GetFileName(dir)) yield return v;
        }
    }

    /// <summary>複数のウィンドウ（プロセス）が同時にダウンロードしないための排他。取れなければ null。</summary>
    public IDisposable? TryAcquireDownloadLock()
    {
        try
        {
            Directory.CreateDirectory(Root);
            return new FileStream(Path.Combine(Root, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteLastResult(UpdateRunResult result)
    {
        WriteJsonAtomically(Path.Combine(Root, LastResultFileName), JsonSerializer.SerializeToUtf8Bytes(result, UpdateJsonContext.Default.UpdateRunResult));
    }

    /// <summary>更新プログラムが残した結果を読み、一度だけ表示するために消す。</summary>
    public UpdateRunResult? TakeLastResult()
    {
        var path = Path.Combine(Root, LastResultFileName);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateRunResult);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("更新の結果を読み取れませんでした。", e);
            return null;
        }
    }

    private static void WriteJsonAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>ダウンロード済みの更新ファイルの検証（サイズ・SHA-256・署名）。アプリと更新プログラムの両方で使う。</summary>
public static class UpdatePackageVerifier
{
    /// <summary>
    /// 開いているストリームを検証する。更新プログラムは、このストリームを（書き込みを禁止したまま）開いたまま
    /// msiexec に渡すことで、検証後のすり替えを防ぐ。
    /// </summary>
    public static async Task<bool> VerifyAsync(Stream installer, string fileName, SemanticVersion version, string signatureText,
        string publicKeyPem, byte[]? expectedSha256, CancellationToken cancellationToken)
    {
        installer.Position = 0;
        var size = installer.Length;
        var sha256 = await SHA256.HashDataAsync(installer, cancellationToken);
        installer.Position = 0;
        if (expectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(sha256, expectedSha256)) return false;
        var message = UpdateSignature.BuildMessage(fileName, version, size, sha256);
        return UpdateSignature.Verify(message, signatureText, publicKeyPem);
    }

    public static async Task<bool> VerifyFileAsync(DownloadedUpdate update, string publicKeyPem, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(update.SignaturePath);
            if (info.Length > UpdateSignature.MaxSignatureFileBytes) return false;
            var signature = await File.ReadAllTextAsync(update.SignaturePath, cancellationToken);
            await using var stream = new FileStream(update.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await VerifyAsync(stream, Path.GetFileName(update.InstallerPath), update.Version, signature, publicKeyPem, null, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"更新ファイルを検証できませんでした：{update.Version}", e);
            return false;
        }
    }
}
