using System.Reflection;

namespace MediaViewer.Core.Updates;

/// <summary>
/// 更新の配信元と検証用の公開鍵。リポジトリはビルド設定（AssemblyMetadata "UpdateRepository"）から読む（仕様 §10.1）。
/// </summary>
public sealed record UpdateConfiguration(string Owner, string Repository, string PublicKeyPem, SemanticVersion CurrentVersion)
{
    /// <summary>
    /// 更新ファイルの署名を検証する公開鍵（ECDSA P-256、仕様決定事項 D11）。
    /// 対になる秘密鍵は GitHub Actions の Secrets（UPDATE_SIGNING_KEY）と開発者の手元だけにある。
    /// </summary>
    public const string DefaultPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5eTKBVeWgBWOBdXzP8Fh/qmPSrwZ
        mjhIuc2i5z7KfyKTiDEequXdF+uervL5dcKHZgQpdYBNR5d3T1ctiC/5Xg==
        -----END PUBLIC KEY-----
        """;

    /// <summary>更新ファイル（MSI）の名前。アーキテクチャを含め、想定外のアセットを取り違えない（仕様 §10.3）。</summary>
    public string InstallerAssetName(SemanticVersion version) => $"MediaViewer-{version}-x64.msi";

    public string SignatureAssetName(SemanticVersion version) => InstallerAssetName(version) + ".sig";

    /// <summary>実行中のアプリの設定から作る。リポジトリ指定がなければ null（更新機能を使わない）。</summary>
    public static UpdateConfiguration? FromAssembly(Assembly assembly)
    {
        var repo = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
        if (string.IsNullOrWhiteSpace(repo) || repo.Split('/') is not [var owner, var name]) return null;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!SemanticVersion.TryParse(info, out var current)) return null;
        return new UpdateConfiguration(owner, name, DefaultPublicKeyPem, current);
    }
}
