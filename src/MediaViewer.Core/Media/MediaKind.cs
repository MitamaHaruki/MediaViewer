using System.Collections.Frozen;

namespace MediaViewer.Core.Media;

public enum MediaKind
{
    Unknown,
    Image,
    Video,
    Audio,
}

/// <summary>
/// 拡張子による種別分類。フォルダ一覧・フィルター用の一次判定であり、
/// 実際に開くときはファイル内容（デコーダー）で検査する（仕様決定事項 #3）。
/// </summary>
public static class MediaKinds
{
    private static readonly FrozenDictionary<string, MediaKind> ByExtension = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = MediaKind.Image,
        [".jpg"] = MediaKind.Image,
        [".jpeg"] = MediaKind.Image,
        [".jpe"] = MediaKind.Image,
        [".jfif"] = MediaKind.Image,
        [".bmp"] = MediaKind.Image,
        [".dib"] = MediaKind.Image,
        [".gif"] = MediaKind.Image,
        [".webp"] = MediaKind.Image,
        [".tif"] = MediaKind.Image,
        [".tiff"] = MediaKind.Image,

        [".mp4"] = MediaKind.Video,
        [".m4v"] = MediaKind.Video,
        [".mkv"] = MediaKind.Video,
        [".mov"] = MediaKind.Video,
        [".avi"] = MediaKind.Video,
        [".webm"] = MediaKind.Video,

        [".mp3"] = MediaKind.Audio,
        [".wav"] = MediaKind.Audio,
        [".flac"] = MediaKind.Audio,
        [".m4a"] = MediaKind.Audio,
        [".aac"] = MediaKind.Audio,
        [".ogg"] = MediaKind.Audio,
        [".oga"] = MediaKind.Audio,
        [".opus"] = MediaKind.Audio,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>対応する拡張子と種別の一覧（インストーラーのファイルの関連付けと一致させる）。</summary>
    public static IReadOnlyDictionary<string, MediaKind> Extensions => ByExtension;

    public static MediaKind FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var kind) ? kind : MediaKind.Unknown;
    }

    public static bool IsSupported(string path) => FromPath(path) != MediaKind.Unknown;
}
