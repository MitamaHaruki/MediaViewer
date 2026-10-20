using System.Buffers.Binary;
using System.Text;
using MediaViewer.Imaging.Color;

namespace MediaViewer.Imaging.Decoding;

/// <summary>PNG の cICP チャンク（ITU-T H.273 の符号化指定）。</summary>
public readonly record struct Cicp(byte ColourPrimaries, byte TransferCharacteristics, byte MatrixCoefficients, byte VideoFullRangeFlag);

/// <summary>PNG の色関連チャンク（IDAT より前に置かれるもの）。</summary>
public sealed record PngColorChunks(Cicp? Cicp, bool HasIccp, bool HasSrgb, double? FileGamma, RgbPrimaries? Chromaticities, double? MaxContentLightLevel);

/// <summary>JPEG の EXIF に含まれる色空間の指定。</summary>
public sealed record ExifColorInfo(ushort? ColorSpace, string? InteropIndex);

/// <summary>
/// WIC が扱わない色の指定をファイル先頭から読む。画素データは読まない。
/// 壊れたデータは null を返し、例外にしない（仕様 §15）。
/// </summary>
public static class ColorMetadataReader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxHeaderBytes = 32 * 1024 * 1024;

    public static PngColorChunks? ReadPng(Stream stream)
    {
        try
        {
            Span<byte> sig = stackalloc byte[8];
            if (stream.ReadAtLeast(sig, 8, false) != 8 || !sig.SequenceEqual(PngSignature)) return null;

            Cicp? cicp = null;
            bool iccp = false, srgb = false;
            double? gamma = null, maxCll = null;
            RgbPrimaries? chrm = null;
            long consumed = 8;
            Span<byte> head = stackalloc byte[8];
            while (stream.ReadAtLeast(head, 8, false) == 8)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(head);
                var type = Encoding.ASCII.GetString(head[4..8]);
                consumed += 12 + length;
                if (length > int.MaxValue || consumed > MaxHeaderBytes) break;
                if (type is "IDAT" or "IEND") break;

                switch (type)
                {
                    case "cICP" when length == 4:
                        var c = ReadExact(stream, 4);
                        cicp = new Cicp(c[0], c[1], c[2], c[3]);
                        break;
                    case "gAMA" when length == 4:
                        gamma = BinaryPrimitives.ReadUInt32BigEndian(ReadExact(stream, 4)) / 100000.0;
                        break;
                    case "cHRM" when length == 32:
                        var h = ReadExact(stream, 32);
                        double V(int i) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(i * 4)) / 100000.0;
                        chrm = new RgbPrimaries(new(V(2), V(3)), new(V(4), V(5)), new(V(6), V(7)), new(V(0), V(1)));
                        break;
                    case "cLLi" when length == 8:
                        // 最大輝度（MaxCLL、0.0001 cd/m² 単位）
                        maxCll = BinaryPrimitives.ReadUInt32BigEndian(ReadExact(stream, 8)) / 10000.0;
                        break;
                    default:
                        if (type == "iCCP") iccp = true;
                        if (type == "sRGB") srgb = true;
                        Skip(stream, length);
                        break;
                }
                Skip(stream, 4); // CRC
            }
            return new PngColorChunks(cicp, iccp, srgb, gamma, chrm, maxCll);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>JPEG の APP1（EXIF）から ColorSpace（0xA001）と InteroperabilityIndex（R03 = Adobe RGB）を読む。</summary>
    public static ExifColorInfo? ReadJpegExif(Stream stream)
    {
        try
        {
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return null;
            while (true)
            {
                int marker;
                do { marker = stream.ReadByte(); } while (marker == 0xFF);
                if (marker < 0) return null;
                // 単独マーカー（RSTn など）と、画像データの開始（SOS）以降は探さない
                if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
                if (marker == 0xDA || marker == 0xD9) return null;
                var len = (stream.ReadByte() << 8) | stream.ReadByte();
                if (len < 2) return null;
                var payload = ReadExact(stream, len - 2);
                if (marker == 0xE1 && payload.Length > 14 && payload.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8))
                {
                    return ParseTiff(payload.AsSpan(6));
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static ExifColorInfo? ParseTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return null;
        var ifd0 = U32(tiff, 4, little);
        var exifIfd = FindTag(tiff, ifd0, 0x8769, little)?.Value;
        if (exifIfd is null) return null;
        ushort? colorSpace = FindTag(tiff, exifIfd.Value, 0xA001, little) is { } cs ? (ushort)cs.Value : null;
        string? interop = null;
        if (FindTag(tiff, exifIfd.Value, 0xA005, little) is { } interopIfd
            && FindTag(tiff, interopIfd.Value, 0x0001, little) is { Count: <= 4 } index)
        {
            // 4バイト以下の ASCII は値フィールドにそのまま入る
            var raw = tiff.Slice(index.EntryOffset + 8, 4);
            interop = Encoding.ASCII.GetString(raw).TrimEnd('\0');
        }
        return new ExifColorInfo(colorSpace, interop);
    }

    private readonly record struct TiffEntry(uint Value, uint Count, int EntryOffset);

    private static TiffEntry? FindTag(ReadOnlySpan<byte> tiff, uint ifdOffset, ushort tag, bool little)
    {
        if (ifdOffset + 2 > tiff.Length) return null;
        var count = U16(tiff, (int)ifdOffset, little);
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifdOffset + 2 + i * 12;
            if (at + 12 > tiff.Length) return null;
            if (U16(tiff, at, little) != tag) continue;
            var type = U16(tiff, at + 2, little);
            var n = U32(tiff, at + 4, little);
            // SHORT（型3）は値フィールドの先頭2バイト
            var value = type == 3 ? U16(tiff, at + 8, little) : U32(tiff, at + 8, little);
            return new TiffEntry(value, n, at);
        }
        return null;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);

    private static byte[] ReadExact(Stream s, int count)
    {
        var b = new byte[count];
        s.ReadExactly(b);
        return b;
    }

    private static void Skip(Stream s, long count)
    {
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            var n = s.Read(buffer[..(int)Math.Min(buffer.Length, count)]);
            if (n <= 0) throw new EndOfStreamException();
            count -= n;
        }
    }
}
