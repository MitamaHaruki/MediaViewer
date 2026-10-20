using System.Buffers.Binary;
using System.Text;
using Vortice.WIC;

namespace MediaViewer.Tests.Color;

/// <summary>
/// テスト用の画像ファイルを組み立てる。WIC で符号化し、必要なら PNG チャンクや JPEG の EXIF を差し込む。
/// </summary>
internal static class TestImageFiles
{
    public static byte[] Encode(IWICImagingFactory wic, ContainerFormat container, Guid pixelFormat, int width, int height, byte[] pixels, byte[]? icc = null)
    {
        using var ms = new MemoryStream();
        using (var encoder = wic.CreateEncoder(container, ms, BitmapEncoderCacheOption.NoCache))
        {
            using var frame = encoder.CreateNewFrame(out var props);
            frame.Initialize(props);
            frame.SetSize((uint)width, (uint)height);
            var format = pixelFormat;
            frame.SetPixelFormat(ref format);
            if (format != pixelFormat) throw new InvalidOperationException("エンコーダーがこの画素形式に対応していません");
            if (icc is not null)
            {
                using var cc = wic.CreateColorContext();
                cc.InitializeFromMemory(icc, (uint)icc.Length).CheckError();
                frame.SetColorContexts([cc]);
            }
            var stride = pixels.Length / height;
            frame.WritePixels((uint)height, (uint)stride, pixels);
            frame.Commit();
            encoder.Commit();
            props?.Dispose();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// PNG の色関連チャンク（WIC のエンコーダーが自動で書く sRGB / gAMA / cHRM）を取り除いてから、
    /// IHDR の直後に指定のチャンクを差し込む。
    /// </summary>
    public static byte[] InsertPngChunks(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        png = RemovePngChunks(png, "sRGB", "gAMA", "cHRM");
        const int ihdrEnd = 8 + 4 + 4 + 13 + 4;
        using var ms = new MemoryStream();
        ms.Write(png, 0, ihdrEnd);
        foreach (var (type, data) in chunks)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
            ms.Write(header);
            ms.Write(data);
            var crcInput = header.AsSpan(4).ToArray().Concat(data).ToArray();
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
            ms.Write(crc);
        }
        ms.Write(png, ihdrEnd, png.Length - ihdrEnd);
        return ms.ToArray();
    }

    public static byte[] RemovePngChunks(byte[] png, params string[] types)
    {
        using var ms = new MemoryStream();
        ms.Write(png, 0, 8);
        var at = 8;
        while (at + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var total = 12 + length;
            if (!types.Contains(type)) ms.Write(png, at, total);
            at += total;
        }
        return ms.ToArray();
    }

    public static byte[] U32Be(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    /// <summary>JPEG の SOI の直後に EXIF（APP1）を差し込む。</summary>
    public static byte[] InsertJpegExif(byte[] jpeg, ushort? orientation, ushort? colorSpace, string? interopIndex)
    {
        var tiff = BuildExifTiff(orientation, colorSpace, interopIndex);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        using var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE1);
        var len = payload.Length + 2;
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)len);
        ms.Write(payload);
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    /// <summary>リトルエンディアンの TIFF 構造（IFD0 → Exif IFD → 互換性 IFD）。</summary>
    private static byte[] BuildExifTiff(ushort? orientation, ushort? colorSpace, string? interopIndex)
    {
        var b = new byte[256];
        "II"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 8);

        // IFD0（オフセット8）：Orientation と Exif IFD へのポインタ
        var ifd0 = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        if (orientation is { } o) ifd0.Add((0x0112, 3, 1, o));
        const uint exifOffset = 100;
        const uint interopOffset = 180;
        ifd0.Add((0x8769, 4, 1, exifOffset));
        WriteIfd(b, 8, ifd0);

        var exif = new List<(ushort, ushort, uint, uint)>();
        if (colorSpace is { } cs) exif.Add((0xA001, 3, 1, cs));
        if (interopIndex is not null) exif.Add((0xA005, 4, 1, interopOffset));
        WriteIfd(b, (int)exifOffset, exif);

        if (interopIndex is not null)
        {
            var ascii = Encoding.ASCII.GetBytes(interopIndex.PadRight(3) + "\0");
            var value = BinaryPrimitives.ReadUInt32LittleEndian(ascii);
            WriteIfd(b, (int)interopOffset, [(0x0001, 2, 4, value)]);
        }
        return b;
    }

    private static void WriteIfd(byte[] b, int at, List<(ushort Tag, ushort Type, uint Count, uint Value)> entries)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = b.AsSpan(at + 2 + i * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, entries[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], entries[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], entries[i].Count);
            if (entries[i].Type == 3) BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)entries[i].Value);
            else BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].Value);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 2 + entries.Count * 12), 0);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var d in data)
        {
            crc ^= d;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
