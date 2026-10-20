using System.Text.Json.Serialization;

namespace MediaViewer.Core.Updates;

/// <summary>GitHub REST API のリリース情報（使う項目だけ）。</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
}

/// <summary>ダウンロード済みの更新の情報（更新フォルダの update.json）。変更内容の表示に使う。</summary>
public sealed class DownloadedUpdateInfo
{
    public string Version { get; set; } = "";
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public string? ReleasePageUrl { get; set; }
    public DateTimeOffset DownloadedUtc { get; set; }
}

/// <summary>更新プログラム（Updater）が書く、最後の更新の結果。次の起動時にアプリが表示する。</summary>
public sealed class UpdateRunResult
{
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateRunStatus>))]
    public UpdateRunStatus Status { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
}

public enum UpdateRunStatus
{
    /// <summary>更新できた。</summary>
    Succeeded,

    /// <summary>更新を取りやめた（UAC の拒否、他のインストールの実行中など）。旧版のまま。</summary>
    Cancelled,

    /// <summary>更新に失敗した。Windows Installer の巻き戻しにより旧版のまま。</summary>
    Failed,

    /// <summary>新しい版が正常に起動しなかったため、直前の版に戻した。</summary>
    RolledBack,

    /// <summary>新しい版が正常に起動せず、直前の版にも戻せなかった。</summary>
    RollbackFailed,
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(DownloadedUpdateInfo))]
[JsonSerializable(typeof(UpdateRunResult))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
