using MediaViewer.Core.Media;

namespace MediaViewer.Core.Conversion;

/// <summary>変換の出力形式（仕様 §3.1 の対応形式）。</summary>
public enum ConversionFormat
{
    Png,
    Jpeg,
    Bmp,
    Gif,
    WebP,
    Tiff,
    Mp4,
    Mkv,
    Mov,
    Avi,
    WebM,
    Mp3,
    Wav,
    Flac,
    M4a,
    Ogg,
}

public static class ConversionFormats
{
    public static string Extension(this ConversionFormat f) => f switch
    {
        ConversionFormat.Png => ".png",
        ConversionFormat.Jpeg => ".jpg",
        ConversionFormat.Bmp => ".bmp",
        ConversionFormat.Gif => ".gif",
        ConversionFormat.WebP => ".webp",
        ConversionFormat.Tiff => ".tif",
        ConversionFormat.Mp4 => ".mp4",
        ConversionFormat.Mkv => ".mkv",
        ConversionFormat.Mov => ".mov",
        ConversionFormat.Avi => ".avi",
        ConversionFormat.WebM => ".webm",
        ConversionFormat.Mp3 => ".mp3",
        ConversionFormat.Wav => ".wav",
        ConversionFormat.Flac => ".flac",
        ConversionFormat.M4a => ".m4a",
        _ => ".ogg",
    };

    public static string DisplayName(this ConversionFormat f) => f switch
    {
        ConversionFormat.Jpeg => "JPEG",
        ConversionFormat.Tiff => "TIFF",
        ConversionFormat.WebP => "WebP",
        ConversionFormat.WebM => "WebM",
        ConversionFormat.M4a => "M4A（AAC）",
        _ => f.ToString().ToUpperInvariant(),
    };

    public static MediaKind Kind(this ConversionFormat f) => f switch
    {
        <= ConversionFormat.Tiff => MediaKind.Image,
        <= ConversionFormat.WebM => MediaKind.Video,
        _ => MediaKind.Audio,
    };

    /// <summary>
    /// 入力の種類に対して選べる出力形式。種類をまたぐ変換（動画→音声など）は初期版の対象外（仕様 §9.2）。
    /// 入力と同じ形式は変換の意味がないため除く。
    /// </summary>
    public static IReadOnlyList<ConversionFormat> TargetsFor(MediaKind kind, string inputPath)
    {
        var current = FromPath(inputPath);
        return Enum.GetValues<ConversionFormat>()
            .Where(f => f.Kind() == kind && f != current)
            .ToList();
    }

    /// <summary>拡張子から形式を求める（同じ形式の別名の拡張子も含む）。</summary>
    public static ConversionFormat? FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ConversionFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ConversionFormat.Jpeg,
        ".bmp" or ".dib" => ConversionFormat.Bmp,
        ".gif" => ConversionFormat.Gif,
        ".webp" => ConversionFormat.WebP,
        ".tif" or ".tiff" => ConversionFormat.Tiff,
        ".mp4" or ".m4v" => ConversionFormat.Mp4,
        ".mkv" => ConversionFormat.Mkv,
        ".mov" => ConversionFormat.Mov,
        ".avi" => ConversionFormat.Avi,
        ".webm" => ConversionFormat.WebM,
        ".mp3" => ConversionFormat.Mp3,
        ".wav" => ConversionFormat.Wav,
        ".flac" => ConversionFormat.Flac,
        ".m4a" or ".aac" => ConversionFormat.M4a,
        ".ogg" or ".oga" or ".opus" => ConversionFormat.Ogg,
        _ => null,
    };

    /// <summary>FFmpeg の出力フォーマット名（-f に渡す）。</summary>
    public static string FfmpegMuxer(this ConversionFormat f) => f switch
    {
        ConversionFormat.Mp4 => "mp4",
        ConversionFormat.Mkv => "matroska",
        ConversionFormat.Mov => "mov",
        ConversionFormat.Avi => "avi",
        ConversionFormat.WebM => "webm",
        ConversionFormat.Mp3 => "mp3",
        ConversionFormat.Wav => "wav",
        ConversionFormat.Flac => "flac",
        ConversionFormat.M4a => "ipod",
        ConversionFormat.Ogg => "ogg",
        ConversionFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, "FFmpeg では書き出さない形式です"),
    };
}
