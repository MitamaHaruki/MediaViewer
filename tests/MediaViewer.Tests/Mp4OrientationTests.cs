using System.Buffers.Binary;
using System.Text;
using MediaViewer.Core.Media;

namespace MediaViewer.Tests;

[TestClass]
public sealed class Mp4OrientationTests
{
    private const int One = 0x10000;  // 16.16 固定小数点の 1

    [TestMethod]
    [DataRow(One, 0, 0, One, 0)]
    [DataRow(0, One, -One, 0, 90)]
    [DataRow(-One, 0, 0, -One, 180)]
    [DataRow(0, -One, One, 0, 270)]
    public void 映像トラックの表示行列から回転を読む(int a, int b, int c, int d, int expected)
    {
        var file = Mp4(Trak("soun", One, 0, 0, One), Trak("vide", a, b, c, d));
        Assert.AreEqual(expected, Mp4Orientation.Read(new MemoryStream(file)));
    }

    [TestMethod]
    public void 音声トラックの行列は使わない()
    {
        var file = Mp4(Trak("soun", 0, One, -One, 0));
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(file)));
    }

    [TestMethod]
    public void バージョン1の見出しも読める()
    {
        var file = Mp4(Trak("vide", 0, One, -One, 0, tkhdVersion: 1));
        Assert.AreEqual(90, Mp4Orientation.Read(new MemoryStream(file)));
    }

    [TestMethod]
    public void moovがメディアデータの後ろにあっても読める()
    {
        // 64 ビットのサイズ（size = 1）を使う mdat の後ろに moov を置く
        var mdatPayload = new byte[1000];
        var mdat = new byte[16 + mdatPayload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(mdat, 4);
        BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
        var file = Concat(Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom")), mdat, Box("moov", Trak("vide", -One, 0, 0, -One)));
        Assert.AreEqual(180, Mp4Orientation.Read(new MemoryStream(file)));
    }

    [TestMethod]
    public void 九十度単位でない回転や反転は回転なしとして扱う()
    {
        var c45 = (int)(One * Math.Cos(Math.PI / 4));
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(Mp4(Trak("vide", c45, c45, -c45, c45)))));
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(Mp4(Trak("vide", -One, 0, 0, One)))), "左右反転");
    }

    [TestMethod]
    public void 壊れたデータや別の形式では0を返す()
    {
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream([])));
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(Encoding.ASCII.GetBytes("RIFF....WEBPVP8 "))));
        var file = Mp4(Trak("vide", 0, One, -One, 0));
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(file[..(file.Length - 40)])), "途中で切れている");
        var random = new byte[4096];
        new Random(1).NextBytes(random);
        Assert.AreEqual(0, Mp4Orientation.Read(new MemoryStream(random)));
        Assert.AreEqual(0, Mp4Orientation.Read(Path.Combine(Path.GetTempPath(), "存在しない-" + Guid.NewGuid() + ".mp4")));
    }

    // ---- MP4 の組み立て ----

    private static byte[] Mp4(params byte[][] traks) =>
        Concat(Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom")), Box("moov", [Box("mvhd", new byte[100]), .. traks]));

    private static byte[] Trak(string handler, int a, int b, int c, int d, int tkhdVersion = 0) =>
        Box("trak", Tkhd(a, b, c, d, tkhdVersion), Box("mdia", Box("mdhd", new byte[24]), Hdlr(handler)));

    private static byte[] Tkhd(int a, int b, int c, int d, int version)
    {
        var times = version == 1 ? 32 : 20;
        var payload = new byte[4 + times + 16 + 36 + 8];
        payload[0] = (byte)version;
        var m = payload.AsSpan(4 + times + 16);
        int[] matrix = [a, b, 0, c, d, 0, 0, 0, 0x40000000];
        for (var i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(m[(i * 4)..], matrix[i]);
        return Box("tkhd", payload);
    }

    private static byte[] Hdlr(string handler)
    {
        var payload = new byte[4 + 4 + 4 + 12 + 1];
        Encoding.ASCII.GetBytes(handler).CopyTo(payload, 8);
        return Box("hdlr", payload);
    }

    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = Concat(payload);
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
