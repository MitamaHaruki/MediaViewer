using System.Buffers.Binary;

namespace MediaViewer.Core.Media;

/// <summary>
/// MP4・MOV（ISO BMFF）の映像トラックの表示行列（tkhd の matrix）から、表示時の回転角を読む。
/// Windows のプロパティシステム（System.Video.Orientation）と同じ値を返す。プロパティシステムは
/// 読むたびにハンドルが残り、動画を開くたびに増え続けたため、ファイルを直接読む（Phase 6 の測定）。
/// 必要な箱（box）の見出しと tkhd だけを読み、サンプル表などの大きなデータは読まない。
/// </summary>
public static class Mp4Orientation
{
    /// <summary>時計回りの回転角（0、90、180、270）。MP4・MOV 以外や読めない場合は 0。</summary>
    public static int Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static int Read(Stream stream)
    {
        try
        {
            var moov = Children(stream, 0, stream.Length).FirstOrDefault(b => b.Type == "moov");
            if (moov.Type is null) return 0;
            foreach (var trak in Children(stream, moov.Start, moov.End).Where(b => b.Type == "trak"))
            {
                var boxes = Children(stream, trak.Start, trak.End).ToList();
                var mdia = boxes.FirstOrDefault(b => b.Type == "mdia");
                var tkhd = boxes.FirstOrDefault(b => b.Type == "tkhd");
                if (mdia.Type is null || tkhd.Type is null || !IsVideoTrack(stream, mdia)) continue;
                return RotationFromTrackHeader(stream, tkhd);
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException)
        {
            // 壊れたファイルは回転なしとして扱う（再生側でエラーにする）
        }
        return 0;
    }

    /// <summary>
    /// 表示行列 [a b u; c d v; x y w] の回転部分（16.16 固定小数点）から角度を決める。
    /// 90 度単位以外の回転・拡大縮小・反転は回転なしとして扱う。
    /// </summary>
    internal static int RotationFromMatrix(int a, int b, int c, int d) => (Sign(a), Sign(b), Sign(c), Sign(d)) switch
    {
        (1, 0, 0, 1) => 0,
        (0, 1, -1, 0) => 90,
        (-1, 0, 0, -1) => 180,
        (0, -1, 1, 0) => 270,
        _ => 0,
    };

    private static int Sign(int value) => Math.Abs(value) < 0x8000 ? 0 : Math.Sign(value);

    private static int RotationFromTrackHeader(Stream stream, Box tkhd)
    {
        // version(1) flags(3)、日時・トラック番号・長さ（version 0 は 20 バイト、1 は 32 バイト）、
        // 予約 8・layer 2・alternate_group 2・volume 2・予約 2 の後に matrix（4 バイト × 9）
        Span<byte> header = stackalloc byte[4];
        stream.Position = tkhd.Start;
        stream.ReadExactly(header);
        var skip = (header[0] == 1 ? 32 : 20) + 16;
        if (tkhd.Start + 4 + skip + 36 > tkhd.End) return 0;
        Span<byte> matrix = stackalloc byte[36];
        stream.Position = tkhd.Start + 4 + skip;
        stream.ReadExactly(matrix);
        var a = BinaryPrimitives.ReadInt32BigEndian(matrix);
        var b = BinaryPrimitives.ReadInt32BigEndian(matrix[4..]);
        var c = BinaryPrimitives.ReadInt32BigEndian(matrix[12..]);
        var d = BinaryPrimitives.ReadInt32BigEndian(matrix[16..]);
        return RotationFromMatrix(a, b, c, d);
    }

    private static bool IsVideoTrack(Stream stream, Box mdia)
    {
        var hdlr = Children(stream, mdia.Start, mdia.End).FirstOrDefault(b => b.Type == "hdlr");
        if (hdlr.Type is null || hdlr.End - hdlr.Start < 12) return false;
        // version(1) flags(3) pre_defined(4) handler_type(4)
        Span<byte> buffer = stackalloc byte[12];
        stream.Position = hdlr.Start;
        stream.ReadExactly(buffer);
        return buffer[8] == (byte)'v' && buffer[9] == (byte)'i' && buffer[10] == (byte)'d' && buffer[11] == (byte)'e';
    }

    private readonly record struct Box(string Type, long Start, long End);

    /// <summary>範囲内の箱を順に返す（中身は読まず、見出しだけを読んで次へ進む）。</summary>
    private static IEnumerable<Box> Children(Stream stream, long start, long end)
    {
        var position = start;
        var buffer = new byte[16];
        while (position + 8 <= end)
        {
            stream.Position = position;
            stream.ReadExactly(buffer, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            var type = System.Text.Encoding.ASCII.GetString(buffer, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(buffer, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || position + size > end) yield break;
            yield return new Box(type, position + headerSize, position + size);
            position += size;
        }
    }
}
