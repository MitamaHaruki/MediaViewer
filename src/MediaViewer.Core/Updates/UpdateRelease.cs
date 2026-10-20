namespace MediaViewer.Core.Updates;

/// <summary>ダウンロードしてよいと確認できたリリースアセット。</summary>
public sealed record ReleaseAsset(string Name, long Size, Uri Url, byte[]? Sha256);

/// <summary>検証済みのリリース（安定版、正規のリポジトリ、想定どおりのアセット名とURL）。</summary>
public sealed record UpdateRelease(
    SemanticVersion Version,
    string TagName,
    string Title,
    string Notes,
    Uri? ReleasePage,
    ReleaseAsset Installer,
    ReleaseAsset Signature)
{
    /// <summary>MSI として受け入れる最大サイズ。これを超えるアセットはダウンロードしない。</summary>
    public const long MaxInstallerBytes = 1L << 30;

    /// <summary>
    /// GitHub のリリース情報を検査する（仕様 §10.1、§10.3）。Release JSON の URL は、
    /// 正規のリポジトリのダウンロード用パスと完全に一致する場合だけ使う。
    /// </summary>
    public static UpdateRelease? TryCreate(GitHubRelease release, UpdateConfiguration config, out string reason)
    {
        if (release.Draft || release.Prerelease)
        {
            reason = "下書きまたはプレリリースのため対象外です。";
            return null;
        }
        if (release.TagName is not { } tag || !tag.StartsWith('v')
            || !SemanticVersion.TryParse(tag, out var version) || version.IsPreRelease)
        {
            reason = $"タグ名がバージョン番号の形式ではありません：{release.TagName}";
            return null;
        }

        var installer = FindAsset(release, config, tag, config.InstallerAssetName(version), MaxInstallerBytes, out reason);
        if (installer is null) return null;
        if (installer.Size <= 0)
        {
            reason = "更新ファイルのサイズが不正です。";
            return null;
        }
        var signature = FindAsset(release, config, tag, config.SignatureAssetName(version), UpdateSignature.MaxSignatureFileBytes, out reason);
        if (signature is null) return null;

        Uri? page = null;
        if (Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html)
            && html.Scheme == Uri.UriSchemeHttps && html.IsDefaultPort
            && string.Equals(html.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && html.AbsolutePath.StartsWith($"/{config.Owner}/{config.Repository}/releases/", StringComparison.OrdinalIgnoreCase))
        {
            page = html;
        }

        reason = "";
        return new UpdateRelease(version, tag, string.IsNullOrWhiteSpace(release.Name) ? tag : release.Name.Trim(),
            release.Body?.Trim() ?? "", page, installer, signature);
    }

    private static ReleaseAsset? FindAsset(GitHubRelease release, UpdateConfiguration config, string tag, string name, long maxBytes, out string reason)
    {
        var matches = (release.Assets ?? []).Where(a => a.Name == name).ToList();
        if (matches.Count != 1)
        {
            reason = matches.Count == 0 ? $"アセット {name} がありません。" : $"アセット {name} が複数あります。";
            return null;
        }
        var asset = matches[0];
        if (asset.State is { } state && state != "uploaded")
        {
            reason = $"アセット {name} のアップロードが完了していません。";
            return null;
        }
        if (asset.Size < 0 || asset.Size > maxBytes)
        {
            reason = $"アセット {name} のサイズが想定外です（{asset.Size} バイト）。";
            return null;
        }
        var expected = new Uri($"https://github.com/{config.Owner}/{config.Repository}/releases/download/{tag}/{name}");
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.Query.Length > 0 || url.Fragment.Length > 0
            || !string.Equals(url.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(url.AbsolutePath, expected.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"アセット {name} のダウンロード先が正規のリポジトリではありません。";
            return null;
        }
        byte[]? sha256 = null;
        if (asset.Digest is { Length: > 0 } digest)
        {
            if (!TryParseDigest(digest, out sha256, out var known))
            {
                reason = $"アセット {name} のハッシュ値の形式が不正です。";
                return null;
            }
            if (!known) sha256 = null;
        }
        reason = "";
        return new ReleaseAsset(name, asset.Size, expected, sha256);
    }

    /// <summary>"sha256:16進数" を読む。別のアルゴリズムなら known=false（照合しない）。</summary>
    internal static bool TryParseDigest(string digest, out byte[]? sha256, out bool known)
    {
        sha256 = null;
        known = false;
        var colon = digest.IndexOf(':');
        if (colon <= 0) return false;
        if (!digest.AsSpan(0, colon).Equals("sha256", StringComparison.OrdinalIgnoreCase)) return true;
        var hex = digest[(colon + 1)..];
        if (hex.Length != 64) return false;
        try
        {
            sha256 = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }
        known = true;
        return true;
    }
}
