using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaViewer.Core.Updates;

namespace MediaViewer.Tests.Updates;

/// <summary>テスト用の署名鍵・リリース情報・偽の HTTP 応答。</summary>
internal sealed class UpdateTestFixture : IDisposable
{
    public const string Owner = "TestOwner";
    public const string Repo = "TestRepo";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateTestFixture(string current = "1.0.0")
    {
        Config = new UpdateConfiguration(Owner, Repo, _key.ExportSubjectPublicKeyInfoPem(), SemanticVersion.Parse(current));
        Root = Path.Combine(Path.GetTempPath(), "MediaViewerTests", "updates-" + Guid.NewGuid().ToString("N")[..8]);
        Cache = new UpdateCache(Root);
    }

    public UpdateConfiguration Config { get; }
    public string Root { get; }
    public UpdateCache Cache { get; }
    public FakeHandler Handler { get; } = new();

    public string PrivateKeyPem => _key.ExportPkcs8PrivateKeyPem();

    public UpdateClient CreateClient() => new(Config, Cache, Handler);

    public static byte[] MakeInstaller(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public string Sign(string fileName, string version, byte[] content) =>
        UpdateSignature.Sign(UpdateSignature.BuildMessage(fileName, SemanticVersion.Parse(version), content.Length, SHA256.HashData(content)), PrivateKeyPem);

    /// <summary>MSI と署名を添付したリリースを、偽の GitHub に登録する。</summary>
    public GitHubRelease PublishRelease(string version, byte[] installer, string? signature = null, bool withDigest = true, string? tag = null)
    {
        tag ??= "v" + version;
        var msiName = $"MediaViewer-{version}-x64.msi";
        signature ??= Sign(msiName, version, installer);
        var sigBytes = Encoding.ASCII.GetBytes(signature);
        var release = new GitHubRelease
        {
            TagName = tag,
            Name = $"メディアビューワー {version}",
            Body = "変更内容",
            HtmlUrl = $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}",
            Assets =
            [
                Asset(tag, msiName, installer, withDigest),
                Asset(tag, msiName + ".sig", sigBytes, withDigest),
            ],
        };
        // GitHub と同じく、github.com のダウンロードURLは別ホストへリダイレクトされる
        Handler.Redirect($"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{msiName}", $"https://release-assets.githubusercontent.com/x/{msiName}?sig=1");
        Handler.Ok($"https://release-assets.githubusercontent.com/x/{msiName}?sig=1", installer);
        Handler.Redirect($"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{msiName}.sig", $"https://release-assets.githubusercontent.com/x/{msiName}.sig");
        Handler.Ok($"https://release-assets.githubusercontent.com/x/{msiName}.sig", sigBytes);
        Handler.Ok($"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{tag}", Json(release));
        return release;
    }

    public void SetLatest(GitHubRelease release) =>
        Handler.Ok($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", Json(release));

    public static byte[] Json(GitHubRelease release) =>
        JsonSerializer.SerializeToUtf8Bytes(release, UpdateJsonContext.Default.GitHubRelease);

    private static GitHubAsset Asset(string tag, string name, byte[] content, bool withDigest) => new()
    {
        Name = name,
        Size = content.Length,
        State = "uploaded",
        Digest = withDigest ? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content)) : null,
        BrowserDownloadUrl = $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{name}",
    };

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>URL ごとに決めた応答を返す。要求された URL を記録する。</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<Uri> Requests { get; } = [];

        public void Ok(string url, byte[] content) => _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

        public void Status(string url, HttpStatusCode status, params (string Name, string Value)[] headers) => _routes[url] = () =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        };

        public void Redirect(string url, string location) => _routes[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        };

        public void Fail(string url) => _routes[url] = () => throw new HttpRequestException("接続できません（テスト）");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
                ? Task.FromResult(route())
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
        }
    }
}
