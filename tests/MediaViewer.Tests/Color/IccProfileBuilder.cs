using System.Buffers.Binary;
using System.Text;

namespace MediaViewer.Tests.Color;

/// <summary>
/// テスト用に ICC v2.1 の RGB 表示プロファイル（行列＋単純ガンマ曲線）を生成する。
/// OS同梱のプロファイルに依存せず、任意の原色・ガンマで検証できるようにするためのもの。
/// </summary>
internal static class IccProfileBuilder
{
    public static byte[] BuildMatrixTrc(ColorMath.RgbSpace space, double gamma, string description)
    {
        // ICC の PCS は D50。原色の XYZ を Bradford で D50 へ順応させる
        var toD50 = ColorMath.Bradford(ColorMath.WhiteXyz(space.White), ColorMath.D50Xyz);
        var m = ColorMath.Multiply(toD50, ColorMath.RgbToXyz(space));

        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", TextDescription(description)),
            ("cprt", Text("No copyright, test profile")),
            ("wtpt", Xyz(ColorMath.D50Xyz)),
            ("rXYZ", Xyz([m[0, 0], m[1, 0], m[2, 0]])),
            ("gXYZ", Xyz([m[0, 1], m[1, 1], m[2, 1]])),
            ("bXYZ", Xyz([m[0, 2], m[1, 2], m[2, 2]])),
        };
        var curve = Curve(gamma);

        const int headerSize = 128;
        var tagCount = tags.Count + 3; // rTRC/gTRC/bTRC は同じデータを共有する
        var offset = Align(headerSize + 4 + 12 * tagCount);
        var table = new List<(string Sig, int Offset, int Size)>();
        using var body = new MemoryStream();
        foreach (var (sig, data) in tags)
        {
            table.Add((sig, offset + (int)body.Length, data.Length));
            body.Write(data);
            Pad(body);
        }
        var curveOffset = offset + (int)body.Length;
        body.Write(curve);
        Pad(body);
        foreach (var sig in new[] { "rTRC", "gTRC", "bTRC" })
        {
            table.Add((sig, curveOffset, curve.Length));
        }

        var total = offset + (int)body.Length;
        var profile = new byte[total];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..], (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);
        Ascii(span[12..], "mntr");
        Ascii(span[16..], "RGB ");
        Ascii(span[20..], "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 1);
        Ascii(span[36..], "acsp");
        Ascii(span[40..], "MSFT");
        WriteS15Fixed16(span[68..], ColorMath.D50Xyz[0]);
        WriteS15Fixed16(span[72..], ColorMath.D50Xyz[1]);
        WriteS15Fixed16(span[76..], ColorMath.D50Xyz[2]);

        BinaryPrimitives.WriteUInt32BigEndian(span[headerSize..], (uint)table.Count);
        for (var i = 0; i < table.Count; i++)
        {
            var e = span[(headerSize + 4 + 12 * i)..];
            Ascii(e, table[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], (uint)table[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)table[i].Size);
        }
        body.ToArray().CopyTo(span[offset..]);
        return profile;
    }

    /// <summary>グレーの表示プロファイル（kTRC＝単純ガンマ）。</summary>
    public static byte[] BuildGray(double gamma, string description)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", TextDescription(description)),
            ("cprt", Text("No copyright, test profile")),
            ("wtpt", Xyz(ColorMath.D50Xyz)),
            ("kTRC", Curve(gamma)),
        };
        const int headerSize = 128;
        var offset = Align(headerSize + 4 + 12 * tags.Count);
        var table = new List<(string Sig, int Offset, int Size)>();
        using var body = new MemoryStream();
        foreach (var (sig, data) in tags)
        {
            table.Add((sig, offset + (int)body.Length, data.Length));
            body.Write(data);
            Pad(body);
        }
        var total = offset + (int)body.Length;
        var profile = new byte[total];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);
        Ascii(span[12..], "mntr");
        Ascii(span[16..], "GRAY");
        Ascii(span[20..], "XYZ ");
        Ascii(span[36..], "acsp");
        WriteS15Fixed16(span[68..], ColorMath.D50Xyz[0]);
        WriteS15Fixed16(span[72..], ColorMath.D50Xyz[1]);
        WriteS15Fixed16(span[76..], ColorMath.D50Xyz[2]);
        BinaryPrimitives.WriteUInt32BigEndian(span[headerSize..], (uint)table.Count);
        for (var i = 0; i < table.Count; i++)
        {
            var e = span[(headerSize + 4 + 12 * i)..];
            Ascii(e, table[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], (uint)table[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)table[i].Size);
        }
        body.ToArray().CopyTo(span[offset..]);
        return profile;
    }

    /// <summary>ガンマ値を u8Fixed8 に丸めた実効値（期待値計算で使う）。</summary>
    public static double EffectiveGamma(double gamma) => Math.Round(gamma * 256) / 256.0;

    private static byte[] Xyz(double[] xyz)
    {
        var b = new byte[20];
        Ascii(b, "XYZ ");
        WriteS15Fixed16(b.AsSpan(8), xyz[0]);
        WriteS15Fixed16(b.AsSpan(12), xyz[1]);
        WriteS15Fixed16(b.AsSpan(16), xyz[2]);
        return b;
    }

    private static byte[] Curve(double gamma)
    {
        var b = new byte[14];
        Ascii(b, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return b;
    }

    private static byte[] Text(string s)
    {
        var ascii = Encoding.ASCII.GetBytes(s + "\0");
        var b = new byte[8 + ascii.Length];
        Ascii(b, "text");
        ascii.CopyTo(b, 8);
        return b;
    }

    private static byte[] TextDescription(string s)
    {
        var ascii = Encoding.ASCII.GetBytes(s + "\0");
        var b = new byte[8 + 4 + ascii.Length + 4 + 4 + 2 + 1 + 67];
        Ascii(b, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(b, 12);
        return b;
    }

    private static void WriteS15Fixed16(Span<byte> dst, double v) =>
        BinaryPrimitives.WriteInt32BigEndian(dst, (int)Math.Round(v * 65536));

    private static void Ascii(Span<byte> dst, string s) => Encoding.ASCII.GetBytes(s).CopyTo(dst);

    private static int Align(int v) => (v + 3) & ~3;

    private static void Pad(MemoryStream s)
    {
        while (s.Length % 4 != 0) s.WriteByte(0);
    }
}
