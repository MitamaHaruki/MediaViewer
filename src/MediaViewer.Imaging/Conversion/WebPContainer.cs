using System.Buffers.Binary;
using System.Text;

namespace MediaViewer.Imaging.Conversion;

/// <summary>
/// WebP（RIFF 形式）に ICC プロファイル（ICCP チャンク）を埋め込む。
/// エンコーダー（libwebp）は ICC を書かないため、出力後に拡張形式（VP8X）へ組み替えて追加する。
/// </summary>
public static class WebPContainer
{
    private const byte FlagIcc = 0x20;
    private const byte FlagAlpha = 0x10;

    public static byte[] AddIccProfile(byte[] webp, byte[] icc, int width, int height, bool hasAlpha)
    {
        if (webp.Length < 20 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("WebP の形式が正しくありません。");
        }
        var chunks = ReadChunks(webp);
        var vp8x = chunks.FirstOrDefault(c => c.Type == "VP8X");
        byte[] header;
        if (vp8x.Data is not null)
        {
            header = (byte[])vp8x.Data.Clone();
            header[0] |= FlagIcc;
        }
        else
        {
            header = new byte[10];
            header[0] = (byte)(FlagIcc | (hasAlpha ? FlagAlpha : 0));
            WriteUInt24(header.AsSpan(4), width - 1);
            WriteUInt24(header.AsSpan(7), height - 1);
        }

        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        WriteChunk(body, "VP8X", header);
        WriteChunk(body, "ICCP", icc);
        foreach (var c in chunks.Where(c => c.Type is not ("VP8X" or "ICCP")))
        {
            WriteChunk(body, c.Type, c.Data!);
        }

        var result = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)body.Length);
        body.ToArray().CopyTo(result, 8);
        return result;
    }

    /// <summary>ICCP チャンクがあれば返す（検証用）。</summary>
    public static byte[]? ReadIccProfile(byte[] webp) =>
        ReadChunks(webp).FirstOrDefault(c => c.Type == "ICCP").Data;

    private static List<(string Type, byte[]? Data)> ReadChunks(byte[] webp)
    {
        var chunks = new List<(string, byte[]?)>();
        var riffSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)), (uint)(webp.Length - 8));
        var end = 8 + riffSize;
        var at = 12;
        while (at + 8 <= end)
        {
            var type = Encoding.ASCII.GetString(webp, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > webp.Length) throw new InvalidDataException("WebP のチャンクが壊れています。");
            chunks.Add((type, webp.AsSpan(at + 8, size).ToArray()));
            at += 8 + size + (size & 1);
        }
        return chunks;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        Encoding.ASCII.GetBytes(type, head);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)data.Length);
        s.Write(head);
        s.Write(data);
        if ((data.Length & 1) == 1) s.WriteByte(0);
    }

    private static void WriteUInt24(Span<byte> dst, int value)
    {
        dst[0] = (byte)value;
        dst[1] = (byte)(value >> 8);
        dst[2] = (byte)(value >> 16);
    }
}
