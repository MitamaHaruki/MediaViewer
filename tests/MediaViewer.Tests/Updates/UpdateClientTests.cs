using System.Net;
using System.Text;
using MediaViewer.Core.Updates;

namespace MediaViewer.Tests.Updates;

[TestClass]
public sealed class UpdateClientTests
{
    private const string LatestUrl = $"https://api.github.com/repos/{UpdateTestFixture.Owner}/{UpdateTestFixture.Repo}/releases/latest";

    [TestMethod]
    public async Task Check_NewerStableRelease_IsAvailable()
    {
        using var f = new UpdateTestFixture("1.0.0");
        f.SetLatest(f.PublishRelease("1.2.0", UpdateTestFixture.MakeInstaller(1000)));
        using var client = f.CreateClient();

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.AreEqual(SemanticVersion.Parse("1.2.0"), result.Release!.Version);
        Assert.AreEqual("MediaViewer-1.2.0-x64.msi", result.Release.Installer.Name);
        Assert.AreEqual("変更内容", result.Release.Notes);
        Assert.AreEqual(LatestUrl, f.Handler.Requests.Single().AbsoluteUri);
    }

    [TestMethod]
    [DataRow("1.0.0")]
    [DataRow("0.9.0")]
    public async Task Check_SameOrOlder_IsUpToDate(string latest)
    {
        using var f = new UpdateTestFixture("1.0.0");
        f.SetLatest(f.PublishRelease(latest, UpdateTestFixture.MakeInstaller(10)));
        using var client = f.CreateClient();

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
        Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task Check_NoRelease_IsUpToDate()
    {
        using var f = new UpdateTestFixture();
        using var client = f.CreateClient();
        var result = await client.CheckAsync(CancellationToken.None);
        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
        Assert.IsNull(result.Latest);
    }

    [TestMethod]
    public async Task Check_RateLimited_ThrowsFriendlyError()
    {
        using var f = new UpdateTestFixture();
        f.Handler.Status(LatestUrl, HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1760000000"));
        using var client = f.CreateClient();
        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => client.CheckAsync(CancellationToken.None));
        StringAssert.Contains(e.Message, "制限");
    }

    [TestMethod]
    public async Task Check_Offline_ThrowsFriendlyError()
    {
        using var f = new UpdateTestFixture();
        f.Handler.Fail(LatestUrl);
        using var client = f.CreateClient();
        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => client.CheckAsync(CancellationToken.None));
        StringAssert.Contains(e.Message, "インターネット");
    }

    [TestMethod]
    public async Task Check_NewerReleaseWithoutInstaller_Throws()
    {
        using var f = new UpdateTestFixture("1.0.0");
        var release = f.PublishRelease("2.0.0", UpdateTestFixture.MakeInstaller(10));
        release.Assets!.RemoveAt(0);
        f.SetLatest(release);
        using var client = f.CreateClient();
        await Assert.ThrowsExactlyAsync<UpdateException>(() => client.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Download_VerifiesAndStores()
    {
        using var f = new UpdateTestFixture("1.0.0");
        var installer = UpdateTestFixture.MakeInstaller(300_000);
        f.SetLatest(f.PublishRelease("1.1.0", installer));
        using var client = f.CreateClient();
        var check = await client.CheckAsync(CancellationToken.None);
        var reports = new List<double>();

        var downloaded = await client.DownloadAsync(check.Release!, new SyncProgress(reports.Add), CancellationToken.None);

        CollectionAssert.AreEqual(installer, File.ReadAllBytes(downloaded.InstallerPath));
        Assert.IsTrue(await UpdatePackageVerifier.VerifyFileAsync(downloaded, f.Config.PublicKeyPem, CancellationToken.None));
        Assert.AreEqual(1.0, reports[^1], 1e-9);
        Assert.IsFalse(Directory.GetFiles(f.Cache.DirectoryFor(downloaded.Version), "*.partial").Any());

        var pending = f.Cache.FindPending(f.Config);
        Assert.IsNotNull(pending);
        Assert.AreEqual("変更内容", pending.Info!.Notes);
    }

    [TestMethod]
    public async Task Download_AlreadyVerified_DoesNotDownloadAgain()
    {
        using var f = new UpdateTestFixture("1.0.0");
        f.SetLatest(f.PublishRelease("1.1.0", UpdateTestFixture.MakeInstaller(5000)));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;
        await client.DownloadAsync(release, null, CancellationToken.None);
        var requests = f.Handler.Requests.Count;

        await client.DownloadAsync(release, null, CancellationToken.None);

        Assert.AreEqual(requests, f.Handler.Requests.Count);
    }

    [TestMethod]
    public async Task Download_WrongSignature_IsRejected()
    {
        using var f = new UpdateTestFixture("1.0.0");
        var installer = UpdateTestFixture.MakeInstaller(4000);
        // 別の版として署名されたもの（古い版の正規ファイルを新しい版と偽る攻撃）
        var signature = f.Sign("MediaViewer-1.1.0-x64.msi", "1.0.5", installer);
        f.SetLatest(f.PublishRelease("1.1.0", installer, signature));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;

        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));

        StringAssert.Contains(e.Message, "署名");
        Assert.IsNull(f.Cache.Find(f.Config, release.Version));
        Assert.IsFalse(Directory.GetFiles(f.Cache.DirectoryFor(release.Version)).Any(), "書きかけのファイルを残さない");
    }

    [TestMethod]
    public async Task Download_SignedByOtherKey_IsRejected()
    {
        using var f = new UpdateTestFixture("1.0.0");
        using var other = new UpdateTestFixture("1.0.0");
        var installer = UpdateTestFixture.MakeInstaller(4000);
        f.SetLatest(f.PublishRelease("1.1.0", installer, other.Sign("MediaViewer-1.1.0-x64.msi", "1.1.0", installer)));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;
        await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Download_DigestMismatch_IsRejected()
    {
        using var f = new UpdateTestFixture("1.0.0");
        var installer = UpdateTestFixture.MakeInstaller(4000);
        f.SetLatest(f.PublishRelease("1.1.0", installer));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;
        // 配信途中で中身が変わった（同じサイズの別データ）
        f.Handler.Ok("https://release-assets.githubusercontent.com/x/MediaViewer-1.1.0-x64.msi?sig=1", UpdateTestFixture.MakeInstaller(4000, seed: 2));

        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));
        StringAssert.Contains(e.Message, "ハッシュ");
    }

    [TestMethod]
    public async Task Download_TruncatedOrOversized_IsRejected()
    {
        using var f = new UpdateTestFixture("1.0.0");
        var installer = UpdateTestFixture.MakeInstaller(4000);
        f.SetLatest(f.PublishRelease("1.1.0", installer, withDigest: false));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;
        const string cdn = "https://release-assets.githubusercontent.com/x/MediaViewer-1.1.0-x64.msi?sig=1";

        f.Handler.Ok(cdn, installer[..3000]);
        await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));

        f.Handler.Ok(cdn, [.. installer, 0]);
        await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));

        Assert.IsNull(f.Cache.Find(f.Config, release.Version));
    }

    [TestMethod]
    public async Task Download_RedirectToUnexpectedHost_IsRejected()
    {
        using var f = new UpdateTestFixture("1.0.0");
        f.SetLatest(f.PublishRelease("1.1.0", UpdateTestFixture.MakeInstaller(4000)));
        using var client = f.CreateClient();
        var release = (await client.CheckAsync(CancellationToken.None)).Release!;
        f.Handler.Redirect($"https://github.com/{UpdateTestFixture.Owner}/{UpdateTestFixture.Repo}/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi.sig",
            "http://release-assets.githubusercontent.com/x/MediaViewer-1.1.0-x64.msi.sig");

        var e = await Assert.ThrowsExactlyAsync<UpdateException>(() => client.DownloadAsync(release, null, CancellationToken.None));
        StringAssert.Contains(e.Message, "想定外");
        Assert.IsFalse(f.Handler.Requests.Any(u => u.Scheme == "http"), "HTTP へは接続しない");
    }

    [TestMethod]
    public async Task GetRelease_ForRollback_ReturnsVerifiedRelease()
    {
        using var f = new UpdateTestFixture("1.1.0");
        var installer = UpdateTestFixture.MakeInstaller(2000);
        f.PublishRelease("1.1.0", installer);
        using var client = f.CreateClient();

        var release = await client.GetReleaseAsync(SemanticVersion.Parse("1.1.0"), CancellationToken.None);
        Assert.IsNotNull(release);
        var downloaded = await client.DownloadAsync(release, null, CancellationToken.None);
        Assert.IsNotNull(f.Cache.Find(f.Config, downloaded.Version));
        Assert.IsNull(f.Cache.FindPending(f.Config), "現在の版は「更新」として扱わない");

        Assert.IsNull(await client.GetReleaseAsync(SemanticVersion.Parse("0.5.0"), CancellationToken.None));
    }

    [TestMethod]
    public void AllowedDownloadUrls()
    {
        Assert.IsTrue(UpdateClient.IsAllowedDownloadUrl(new Uri("https://github.com/a/b/releases/download/v1.0.0/x.msi")));
        Assert.IsTrue(UpdateClient.IsAllowedDownloadUrl(new Uri("https://objects.githubusercontent.com/x")));
        Assert.IsTrue(UpdateClient.IsAllowedDownloadUrl(new Uri("https://release-assets.githubusercontent.com/x")));
        Assert.IsFalse(UpdateClient.IsAllowedDownloadUrl(new Uri("http://github.com/x")));
        Assert.IsFalse(UpdateClient.IsAllowedDownloadUrl(new Uri("https://github.com:8443/x")));
        Assert.IsFalse(UpdateClient.IsAllowedDownloadUrl(new Uri("https://evilgithubusercontent.com/x")));
        Assert.IsFalse(UpdateClient.IsAllowedDownloadUrl(new Uri("https://github.com.evil.example/x")));
        Assert.IsFalse(UpdateClient.IsAllowedDownloadUrl(new Uri("https://user@github.com/x")));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

[TestClass]
public sealed class UpdateReleaseTests
{
    private static UpdateConfiguration Config => new(UpdateTestFixture.Owner, UpdateTestFixture.Repo, "", SemanticVersion.Parse("1.0.0"));

    private static GitHubRelease Valid()
    {
        using var f = new UpdateTestFixture();
        return f.PublishRelease("1.1.0", UpdateTestFixture.MakeInstaller(10));
    }

    [TestMethod]
    public void Valid_IsAccepted()
    {
        var release = UpdateRelease.TryCreate(Valid(), Config, out var reason);
        Assert.IsNotNull(release, reason);
        Assert.IsNotNull(release.Installer.Sha256);
        Assert.AreEqual("https://github.com/TestOwner/TestRepo/releases/tag/v1.1.0", release.ReleasePage!.ToString());
    }

    [TestMethod]
    public void DraftOrPrerelease_IsRejected()
    {
        var draft = Valid();
        draft.Draft = true;
        Assert.IsNull(UpdateRelease.TryCreate(draft, Config, out _));
        var pre = Valid();
        pre.Prerelease = true;
        Assert.IsNull(UpdateRelease.TryCreate(pre, Config, out _));
    }

    [TestMethod]
    [DataRow("1.1.0")]
    [DataRow("v1.1.0-beta")]
    [DataRow("latest")]
    public void BadTag_IsRejected(string tag)
    {
        var release = Valid();
        release.TagName = tag;
        Assert.IsNull(UpdateRelease.TryCreate(release, Config, out _));
    }

    [TestMethod]
    [DataRow("https://github.com/Attacker/TestRepo/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi")]
    [DataRow("https://github.com/TestOwner/TestRepo/releases/download/v1.0.0/MediaViewer-1.1.0-x64.msi")]
    [DataRow("http://github.com/TestOwner/TestRepo/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi")]
    [DataRow("https://example.com/TestOwner/TestRepo/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi")]
    [DataRow("https://github.com/TestOwner/TestRepo/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi?x=1")]
    [DataRow("file:///C:/Windows/evil.msi")]
    [DataRow(null)]
    public void UnexpectedDownloadUrl_IsRejected(string? url)
    {
        var release = Valid();
        release.Assets![0].BrowserDownloadUrl = url;
        Assert.IsNull(UpdateRelease.TryCreate(release, Config, out var reason));
        Assert.IsFalse(string.IsNullOrEmpty(reason));
    }

    [TestMethod]
    public void OwnerAndRepo_AreCaseInsensitive()
    {
        var release = Valid();
        release.Assets![0].BrowserDownloadUrl = "https://github.com/testowner/testrepo/releases/download/v1.1.0/MediaViewer-1.1.0-x64.msi";
        Assert.IsNotNull(UpdateRelease.TryCreate(release, Config, out _));
    }

    [TestMethod]
    public void WrongArchitectureOrDuplicateAsset_IsRejected()
    {
        var arm = Valid();
        arm.Assets![0].Name = "MediaViewer-1.1.0-arm64.msi";
        Assert.IsNull(UpdateRelease.TryCreate(arm, Config, out _));

        var dup = Valid();
        dup.Assets!.Add(dup.Assets[0]);
        Assert.IsNull(UpdateRelease.TryCreate(dup, Config, out _));
    }

    [TestMethod]
    public void IncompleteOrHugeAsset_IsRejected()
    {
        var uploading = Valid();
        uploading.Assets![0].State = "starter";
        Assert.IsNull(UpdateRelease.TryCreate(uploading, Config, out _));

        var huge = Valid();
        huge.Assets![1].Size = 1 << 20;
        Assert.IsNull(UpdateRelease.TryCreate(huge, Config, out _));
    }

    [TestMethod]
    public void Digest_Parsing()
    {
        Assert.IsTrue(UpdateRelease.TryParseDigest("sha256:" + new string('a', 64), out var sha, out var known));
        Assert.IsTrue(known);
        Assert.AreEqual(32, sha!.Length);
        Assert.IsTrue(UpdateRelease.TryParseDigest("sha512:abcd", out _, out known));
        Assert.IsFalse(known, "知らないアルゴリズムは照合しない（署名で検証する）");
        Assert.IsFalse(UpdateRelease.TryParseDigest("sha256:xyz", out _, out _));
        Assert.IsFalse(UpdateRelease.TryParseDigest("abcdef", out _, out _));
    }
}

[TestClass]
public sealed class UpdateSignatureTests
{
    [TestMethod]
    public void SignAndVerify_DetectsAnyChange()
    {
        using var f = new UpdateTestFixture();
        var content = UpdateTestFixture.MakeInstaller(100);
        var sha = System.Security.Cryptography.SHA256.HashData(content);
        var v = SemanticVersion.Parse("1.2.3");
        var message = UpdateSignature.BuildMessage("MediaViewer-1.2.3-x64.msi", v, 100, sha);
        var signature = UpdateSignature.Sign(message, f.PrivateKeyPem);

        Assert.IsTrue(UpdateSignature.Verify(message, signature, f.Config.PublicKeyPem));
        Assert.IsFalse(UpdateSignature.Verify(UpdateSignature.BuildMessage("MediaViewer-1.2.3-x64.msi", v, 101, sha), signature, f.Config.PublicKeyPem));
        Assert.IsFalse(UpdateSignature.Verify(UpdateSignature.BuildMessage("MediaViewer-1.2.4-x64.msi", SemanticVersion.Parse("1.2.4"), 100, sha), signature, f.Config.PublicKeyPem));
        sha[0] ^= 1;
        Assert.IsFalse(UpdateSignature.Verify(UpdateSignature.BuildMessage("MediaViewer-1.2.3-x64.msi", v, 100, sha), signature, f.Config.PublicKeyPem));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not base64")]
    [DataRow("AAAA")]
    public void MalformedSignature_IsRejected(string signature)
    {
        using var f = new UpdateTestFixture();
        Assert.IsFalse(UpdateSignature.Verify(Encoding.UTF8.GetBytes("x"), signature, f.Config.PublicKeyPem));
    }

    [TestMethod]
    public void EmbeddedPublicKey_IsP256()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(UpdateConfiguration.DefaultPublicKeyPem);
        Assert.AreEqual(256, key.KeySize);
    }
}

[TestClass]
public sealed class UpdateCacheTests
{
    [TestMethod]
    public void Cleanup_KeepsCurrentNewerAndPrevious()
    {
        using var f = new UpdateTestFixture("1.2.0");
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.1.5", "1.2.0", "1.3.0" })
        {
            Directory.CreateDirectory(f.Cache.DirectoryFor(SemanticVersion.Parse(v)));
        }
        File.WriteAllText(Path.Combine(f.Cache.DirectoryFor(SemanticVersion.Parse("1.3.0")), "x.msi.partial"), "");
        Directory.CreateDirectory(Path.Combine(f.Root, "not-a-version"));

        f.Cache.Cleanup(SemanticVersion.Parse("1.2.0"));

        var left = Directory.GetDirectories(f.Root).Select(Path.GetFileName).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "1.1.5", "1.2.0", "1.3.0", "not-a-version" }, left);
        Assert.IsFalse(Directory.GetFiles(f.Cache.DirectoryFor(SemanticVersion.Parse("1.3.0")), "*.partial").Any());
    }

    [TestMethod]
    public void LastResult_IsShownOnce()
    {
        using var f = new UpdateTestFixture();
        f.Cache.WriteLastResult(new UpdateRunResult { Status = UpdateRunStatus.RolledBack, FromVersion = "1.0.0", ToVersion = "1.1.0", FinishedUtc = DateTimeOffset.UtcNow });

        var result = f.Cache.TakeLastResult();

        Assert.AreEqual(UpdateRunStatus.RolledBack, result!.Status);
        Assert.AreEqual("1.1.0", result.ToVersion);
        Assert.IsNull(f.Cache.TakeLastResult());
    }

    [TestMethod]
    public void DownloadLock_IsExclusive()
    {
        using var f = new UpdateTestFixture();
        using (var first = f.Cache.TryAcquireDownloadLock())
        {
            Assert.IsNotNull(first);
            Assert.IsNull(f.Cache.TryAcquireDownloadLock());
        }
        using var again = f.Cache.TryAcquireDownloadLock();
        Assert.IsNotNull(again);
    }
}
