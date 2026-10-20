using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaViewer.Core.Diagnostics;

namespace MediaViewer.Core.Updates;

/// <summary>更新の確認・ダウンロードの失敗。Message は利用者向けの日本語、Details は調査用（ログへ記録）。</summary>
public sealed class UpdateException(string message, string? details = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Details { get; } = details;
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
}

/// <summary>更新確認の結果。Latest は公開されている最新の安定版（リリースが1つもなければ null）。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, SemanticVersion? Latest, UpdateRelease? Release);

/// <summary>
/// GitHub Releases から更新を確認・ダウンロードする（仕様 §10.2、§10.3）。
/// 通信の失敗・レート制限・オフラインは <see cref="UpdateException"/> として返し、アプリの通常機能には影響させない。
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private const int MaxApiResponseBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly UpdateConfiguration _config;
    private readonly UpdateCache _cache;
    private readonly HttpClient _http;

    public UpdateClient(UpdateConfiguration config, UpdateCache cache, HttpMessageHandler? handler = null)
    {
        _config = config;
        _cache = cache;
        // リダイレクトは1段ずつ行き先を検査するため、自動では追わない
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MediaViewer", config.CurrentVersion.ToString()));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>最新の安定版を確認する（GET /repos/{owner}/{repo}/releases/latest）。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var release = await GetReleaseJsonAsync("releases/latest", cancellationToken);
        if (release is null) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);
        if (SemanticVersion.TryParse(release.TagName, out var latest) && latest <= _config.CurrentVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, latest, null);
        }
        var update = UpdateRelease.TryCreate(release, _config, out var reason)
            ?? throw new UpdateException("新しいバージョンが公開されていますが、更新ファイルを確認できませんでした。", reason);
        return update.Version > _config.CurrentVersion
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update.Version, update)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, update.Version, null);
    }

    /// <summary>指定した版のリリース（ロールバック用に現在の版の MSI を取得するときに使う）。見つからなければ null。</summary>
    public async Task<UpdateRelease?> GetReleaseAsync(SemanticVersion version, CancellationToken cancellationToken)
    {
        var release = await GetReleaseJsonAsync($"releases/tags/v{version}", cancellationToken);
        if (release is null) return null;
        var update = UpdateRelease.TryCreate(release, _config, out var reason);
        if (update is null || update.Version != version)
        {
            AppLog.Warn($"バージョン {version} のリリースを使えません：{reason}");
            return null;
        }
        return update;
    }

    private async Task<GitHubRelease?> GetReleaseJsonAsync(string path, CancellationToken cancellationToken)
    {
        var url = new Uri(ApiBase, $"repos/{Uri.EscapeDataString(_config.Owner)}/{Uri.EscapeDataString(_config.Repository)}/{path}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var values) ? values.FirstOrDefault() : null;
                if (response.StatusCode == HttpStatusCode.TooManyRequests || remaining == "0")
                {
                    throw new UpdateException("GitHub の利用回数の制限に達したため、更新を確認できませんでした。しばらくしてから再度お試しください。",
                        $"HTTP {(int)response.StatusCode}, x-ratelimit-reset={Header(response, "x-ratelimit-reset")}");
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"更新を確認できませんでした（HTTP {(int)response.StatusCode}）。", url.ToString());
            }
            if (response.Content.Headers.ContentLength > MaxApiResponseBytes)
            {
                throw new UpdateException("更新の情報が大きすぎるため、確認を中止しました。");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadLimitedAsync(stream, MaxApiResponseBytes, timeout.Token);
            return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.GitHubRelease)
                ?? throw new UpdateException("更新の情報を読み取れませんでした。");
        }
        catch (JsonException e)
        {
            throw new UpdateException("更新の情報を読み取れませんでした。", null, e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("更新を確認できませんでした。インターネットに接続されているか確認してください。", e.Message, e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("更新の確認がタイムアウトしました。", null, e);
        }
    }

    /// <summary>
    /// 更新ファイル（MSI と署名）をダウンロードし、サイズ・SHA-256・署名を検証してから保存する（仕様 §10.2-6）。
    /// 途中で失敗したら書きかけのファイルを消す。既に検証済みのファイルがあればダウンロードしない。
    /// </summary>
    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = new DownloadedUpdateInfo
        {
            Version = release.Version.ToString(),
            Title = release.Title,
            Notes = release.Notes,
            ReleasePageUrl = release.ReleasePage?.ToString(),
            DownloadedUtc = DateTimeOffset.UtcNow,
        };
        var existing = _cache.Find(_config, release.Version);
        if (existing is not null && await UpdatePackageVerifier.VerifyFileAsync(existing, _config.PublicKeyPem, cancellationToken))
        {
            if (existing.Info is null) _cache.WriteInfo(release.Version, info);
            progress?.Report(1);
            return _cache.Find(_config, release.Version)!;
        }

        Directory.CreateDirectory(_cache.DirectoryFor(release.Version));
        var installerPath = _cache.InstallerPath(_config, release.Version);
        var signaturePath = _cache.SignaturePath(_config, release.Version);
        var partial = installerPath + ".partial";
        try
        {
            var signature = Encoding.ASCII.GetString(await DownloadSmallAsync(release.Signature, cancellationToken));
            var sha256 = await DownloadToFileAsync(release.Installer, partial, progress, cancellationToken);
            if (release.Installer.Sha256 is { } expected && !CryptographicOperations.FixedTimeEquals(sha256, expected))
            {
                throw new UpdateException("ダウンロードした更新ファイルが壊れています（ハッシュ値が一致しません）。", release.Installer.Name);
            }
            var message = UpdateSignature.BuildMessage(release.Installer.Name, release.Version, release.Installer.Size, sha256);
            if (!UpdateSignature.Verify(message, signature, _config.PublicKeyPem))
            {
                throw new UpdateException("更新ファイルの署名を確認できなかったため、使用しません。", release.Installer.Name);
            }
            await File.WriteAllTextAsync(signaturePath, signature, Encoding.ASCII, cancellationToken);
            File.Move(partial, installerPath, overwrite: true);
            _cache.WriteInfo(release.Version, info);
            AppLog.Info($"更新ファイルをダウンロードし、検証しました：{release.Version}");
            return new DownloadedUpdate(release.Version, installerPath, signaturePath, info);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private async Task<byte[]> DownloadSmallAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await GetFollowingRedirectsAsync(asset.Url, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadLimitedAsync(stream, UpdateSignature.MaxSignatureFileBytes, timeout.Token);
            if (bytes.Length != asset.Size) throw new UpdateException("署名ファイルのサイズが一致しません。", asset.Name);
            return bytes;
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("更新ファイルをダウンロードできませんでした。", e.Message, e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("更新ファイルのダウンロードがタイムアウトしました。", null, e);
        }
    }

    /// <summary>ファイルへ保存しながら SHA-256 を計算する。サイズが宣言と違えば中止する。</summary>
    private async Task<byte[]> DownloadToFileAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            using var response = await GetFollowingRedirectsAsync(asset.Url, idle.Token);
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            {
                throw new UpdateException("更新ファイルのサイズが一致しません。", $"{asset.Name}: {length} != {asset.Size}");
            }
            await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var lastPercent = -1;
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                var read = await source.ReadAsync(buffer, idle.Token);
                if (read == 0) break;
                total += read;
                if (total > asset.Size) throw new UpdateException("更新ファイルのサイズが一致しません。", $"{asset.Name}: > {asset.Size}");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                var percent = (int)(total * 100 / asset.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(total / (double)asset.Size);
                }
            }
            if (total != asset.Size) throw new UpdateException("更新ファイルのダウンロードが途中で終わりました。", $"{asset.Name}: {total} != {asset.Size}");
            await target.FlushAsync(cancellationToken);
            return hash.GetHashAndReset();
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("更新ファイルをダウンロードできませんでした。インターネットに接続されているか確認してください。", e.Message, e);
        }
        catch (IOException e)
        {
            throw new UpdateException("更新ファイルのダウンロード中に通信または保存が失敗しました。", e.Message, e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("更新ファイルのダウンロードが途中で止まりました。", null, e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>リダイレクトを1段ずつ検査しながら追う。行き先は HTTPS の GitHub（ダウンロード用ホストを含む）だけ。</summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowedDownloadUrl(url))
            {
                throw new UpdateException("更新ファイルのダウンロード先が想定外のため、中止しました。", url.Host);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new UpdateException($"更新ファイルをダウンロードできませんでした（HTTP {status}）。", url.Host);
            }
            return response;
        }
        throw new UpdateException("更新ファイルのダウンロード先の転送が多すぎるため、中止しました。");
    }

    internal static bool IsAllowedDownloadUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo)
        && (string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maxBytes) throw new UpdateException("受信したデータが大きすぎるため、中止しました。");
            buffer.Write(chunk, 0, read);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>自動確認の間隔（仕様 §10.2-2：成功した確認から約24時間は確認しない）。</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static bool IsCheckDue(DateTimeOffset? lastCheckedUtc, DateTimeOffset now) =>
        lastCheckedUtc is not { } last || now - last >= Interval || last - now > TimeSpan.FromHours(1);
}
