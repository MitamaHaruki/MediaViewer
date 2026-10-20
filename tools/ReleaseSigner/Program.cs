using System.Security.Cryptography;
using MediaViewer.Core.Updates;

// 使い方：
//   ReleaseSigner sign   --file <MSI> --version <1.2.3> --key-env <環境変数名>
//   ReleaseSigner verify --file <MSI> --version <1.2.3>
// sign は <MSI>.sig を書き、アプリに埋め込んだ公開鍵で確かめてから終了する（鍵の取り違えを防ぐ）。
// 秘密鍵は環境変数で受け取り、ファイルにもログにも書かない。

if (args.Length == 0 || args[0] is not ("sign" or "verify"))
{
    return Usage();
}
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
if (!options.TryGetValue("--file", out var file) || !File.Exists(file)
    || !options.TryGetValue("--version", out var versionText) || !SemanticVersion.TryParse(versionText, out var version))
{
    return Usage();
}

var name = Path.GetFileName(file);
var expectedName = $"MediaViewer-{version}-x64.msi";
if (name != expectedName)
{
    Console.Error.WriteLine($"ファイル名が想定と違います：{name}（想定 {expectedName}）");
    return 1;
}

byte[] sha256;
long size;
await using (var stream = File.OpenRead(file))
{
    size = stream.Length;
    sha256 = await SHA256.HashDataAsync(stream);
}
var message = UpdateSignature.BuildMessage(name, version, size, sha256);
var signaturePath = file + ".sig";

if (args[0] == "sign")
{
    if (!options.TryGetValue("--key-env", out var keyEnv) || Environment.GetEnvironmentVariable(keyEnv) is not { Length: > 0 } privateKey)
    {
        Console.Error.WriteLine("秘密鍵を環境変数（--key-env）で指定してください。");
        return 1;
    }
    await File.WriteAllTextAsync(signaturePath, UpdateSignature.Sign(message, privateKey));
    Console.WriteLine($"署名しました：{Path.GetFileName(signaturePath)}");
}

var signature = await File.ReadAllTextAsync(signaturePath);
if (!UpdateSignature.Verify(message, signature, UpdateConfiguration.DefaultPublicKeyPem))
{
    Console.Error.WriteLine("署名を確認できません（アプリに埋め込んだ公開鍵と対になる秘密鍵で署名されていません）。");
    return 1;
}
Console.WriteLine($"署名を確認しました：{name}（{size:N0} バイト、SHA-256 {Convert.ToHexStringLower(sha256)}）");
return 0;

static int Usage()
{
    Console.Error.WriteLine("使い方：ReleaseSigner sign --file <MSI> --version <x.y.z> --key-env <環境変数名>");
    Console.Error.WriteLine("        ReleaseSigner verify --file <MSI> --version <x.y.z>");
    return 2;
}
