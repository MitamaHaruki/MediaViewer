using System.Globalization;
using System.Text.Json;

namespace MediaViewer.Core.Conversion;

/// <summary>ffprobe で調べたストリーム（仕様 §9.2：ストリーム構成・コーデック・時間長）。</summary>
public sealed record ProbeStream(
    int Index,
    string CodecType,
    string CodecName,
    int Width,
    int Height,
    int Channels,
    int BitsPerSample,
    bool IsAttachedPicture,
    string? Language,
    string? Title)
{
    public bool IsVideo => CodecType == "video" && !IsAttachedPicture;
    public bool IsCoverArt => CodecType == "video" && IsAttachedPicture;
    public bool IsAudio => CodecType == "audio";
    public bool IsSubtitle => CodecType == "subtitle";
    public bool IsAttachment => CodecType == "attachment";
}

public sealed record ProbeResult(string FormatName, TimeSpan Duration, IReadOnlyList<ProbeStream> Streams)
{
    public IEnumerable<ProbeStream> Video => Streams.Where(s => s.IsVideo);
    public IEnumerable<ProbeStream> Audio => Streams.Where(s => s.IsAudio);
    public IEnumerable<ProbeStream> Subtitles => Streams.Where(s => s.IsSubtitle);
    public IEnumerable<ProbeStream> Attachments => Streams.Where(s => s.IsAttachment);
    public ProbeStream? CoverArt => Streams.FirstOrDefault(s => s.IsCoverArt);

    /// <summary>ffprobe の JSON 出力（-print_format json -show_format -show_streams）を読む。</summary>
    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<ProbeStream>();
        if (root.TryGetProperty("streams", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
                var tags = s.TryGetProperty("tags", out var t) ? t : default;
                streams.Add(new ProbeStream(
                    Index: Int(s, "index"),
                    CodecType: Str(s, "codec_type") ?? "",
                    CodecName: Str(s, "codec_name") ?? "",
                    Width: Int(s, "width"),
                    Height: Int(s, "height"),
                    Channels: Int(s, "channels"),
                    BitsPerSample: Math.Max(Int(s, "bits_per_raw_sample"), Int(s, "bits_per_sample")),
                    IsAttachedPicture: disposition.ValueKind == JsonValueKind.Object && Int(disposition, "attached_pic") == 1,
                    Language: tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : null,
                    Title: tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null));
            }
        }
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var duration = format.ValueKind == JsonValueKind.Object && Str(format, "duration") is { } ds
            && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
        var formatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") ?? "" : "";
        return new ProbeResult(formatName, duration, streams);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)) return j;
        return 0;
    }
}
