using System.Security.Cryptography;
using System.Text;

namespace MediaViewer.Core.Updates;

/// <summary>
/// 更新ファイルの署名（仕様決定事項 D11）。ECDSA P-256 / SHA-256、署名は IEEE P1363 形式（64バイト）を Base64 で .sig に保存する。
/// 署名する内容はファイルそのものではなく、ファイル名・バージョン・サイズ・SHA-256 をまとめた短い文書とし、
/// 古い版の正規ファイルを新しい版と偽って配る（ダウングレード）攻撃も防ぐ。
/// </summary>
public static class UpdateSignature
{
    private const string Header = "MediaViewer-Update-Signature-1";
    private const int SignatureLength = 64;

    /// <summary>.sig ファイルの最大サイズ。これを超えるものは読まない。</summary>
    public const int MaxSignatureFileBytes = 4096;

    public static byte[] BuildMessage(string fileName, SemanticVersion version, long size, ReadOnlySpan<byte> sha256)
    {
        var text = new StringBuilder()
            .Append(Header).Append('\n')
            .Append("file=").Append(fileName).Append('\n')
            .Append("version=").Append(version).Append('\n')
            .Append("size=").Append(size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n')
            .Append("sha256=").Append(Convert.ToHexStringLower(sha256)).Append('\n')
            .ToString();
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>リリース作業用（tools/ReleaseSigner）。秘密鍵で署名し、.sig ファイルの中身（Base64）を返す。</summary>
    public static string Sign(byte[] message, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature) + "\n";
    }

    public static bool Verify(byte[] message, string signatureFileText, string publicKeyPem)
    {
        if (!TryDecode(signatureFileText, out var signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256) return false;
            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryDecode(string text, out byte[] signature)
    {
        signature = [];
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 200) return false;
        var buffer = new byte[SignatureLength + 8];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written) || written != SignatureLength) return false;
        signature = buffer[..SignatureLength];
        return true;
    }
}
