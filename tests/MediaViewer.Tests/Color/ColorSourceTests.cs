using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Decoding;
using MediaViewer.Imaging.Graphics;
using Vortice.WIC;

namespace MediaViewer.Tests.Color;

/// <summary>
/// ICC 以外の色指定（グレーICC・CMYK・PNG の cICP / gAMA / cHRM・HDR の PQ・EXIF）と EXIF の向きの検証。
/// </summary>
[TestClass]
public sealed class ColorSourceTests
{
    private const double Tolerance = 0.005;
    private static GraphicsDevice s_device = null!;
    private static string s_dir = null!;

    private static readonly DisplayColorInfo Acm = new(DisplayColorMode.WideColorGamut, 80, null, "test ACM");

    private static IWICImagingFactory Wic => s_device.WicFactory;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_device = GraphicsDevice.Create();
        s_dir = Path.Combine(Path.GetTempPath(), "MediaViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(s_dir);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        s_device.Dispose();
        try { Directory.Delete(s_dir, true); } catch (IOException) { }
    }

    // ---- グレー ----

    [TestMethod]
    public void グレーICCの画像はkTRCどおりの明るさの無彩色になる()
    {
        var icc = IccProfileBuilder.BuildGray(1.8, "Test gray 1.8");
        byte[] gray = [0, 64, 128, 255];
        using var image = Load(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format8bppGray, 4, 1, gray, icc), ".png");

        Assert.AreEqual(SourceColorKind.GrayIcc, image.Color.Kind);
        var px = Render(image, Acm);
        var gamma = IccProfileBuilder.EffectiveGamma(1.8);
        for (var i = 0; i < gray.Length; i++)
        {
            var expected = Math.Pow(gray[i] / 255.0, gamma);
            for (var c = 0; c < 3; c++) Assert.AreEqual(expected, px[i][c], Tolerance, $"gray {gray[i]} ch{c}");
        }
    }

    // ---- CMYK ----

    private static readonly byte[] CmykPixels =
    [
        0, 0, 0, 0,       // 用紙の白
        0, 0, 0, 255,     // K100
        255, 0, 0, 0,     // C100
        0, 255, 0, 0,     // M100
    ];

    [TestMethod]
    public void CMYK画像は埋め込みICCで広色域RGBへ変換される()
    {
        var swop = SystemProfile("RSWOP.icm");
        using var image = Load(TestImageFiles.Encode(Wic, ContainerFormat.Tiff, PixelFormat.Format32bppCMYK, 4, 1, CmykPixels, swop), ".tif");

        Assert.AreEqual(SourceColorKind.CmykConverted, image.Color.Kind);
        Assert.IsNull(image.Color.Note);
        AssertCmykLooksRight(Render(image, Acm));
    }

    [TestMethod]
    public void CMYKにICCがなければ既定のSWOPで変換し注記を付ける()
    {
        SystemProfile("RSWOP.icm");
        using var image = Load(TestImageFiles.Encode(Wic, ContainerFormat.Tiff, PixelFormat.Format32bppCMYK, 4, 1, CmykPixels), ".tif");

        Assert.AreEqual(SourceColorKind.CmykConverted, image.Color.Kind);
        StringAssert.Contains(image.Color.Note, "SWOP");
        AssertCmykLooksRight(Render(image, Acm));
    }

    private static void AssertCmykLooksRight(float[][] px)
    {
        // 相対的な色域維持なので、用紙の白はほぼ白になる
        for (var c = 0; c < 3; c++) Assert.AreEqual(1.0, px[0][c], 0.03, $"white ch{c}");
        // K100 は暗い無彩色
        Assert.IsTrue(px[1].Take(3).All(v => v < 0.12), $"K100 = {string.Join(", ", px[1].Take(3))}");
        // シアンは赤が最も小さく、マゼンタは緑が最も小さい
        Assert.IsTrue(px[2][0] < px[2][1] && px[2][0] < px[2][2], $"C100 = {string.Join(", ", px[2].Take(3))}");
        Assert.IsTrue(px[3][1] < px[3][0] && px[3][1] < px[3][2], $"M100 = {string.Join(", ", px[3].Take(3))}");
    }

    // ---- PNG cICP / gAMA / cHRM / sRGB ----

    private static readonly byte[] RgbaPixels = [255, 0, 0, 255, 0, 255, 0, 255, 128, 128, 128, 255]; // R, G, 灰（RGBA）

    private static byte[] RgbaPng(byte[]? icc = null)
    {
        var bgra = new byte[RgbaPixels.Length];
        for (var i = 0; i < RgbaPixels.Length; i += 4)
        {
            bgra[i] = RgbaPixels[i + 2];
            bgra[i + 1] = RgbaPixels[i + 1];
            bgra[i + 2] = RgbaPixels[i];
            bgra[i + 3] = RgbaPixels[i + 3];
        }
        return TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 3, 1, bgra, icc);
    }

    private static double[] Expected(ColorMath.RgbSpace space, Func<double, double> eotf, int pixel)
    {
        var linear = RgbaPixels.Skip(pixel * 4).Take(3).Select(v => eotf(v / 255.0)).ToArray();
        return ColorMath.Convert(space, ColorMath.Srgb, linear);
    }

    private static void AssertRgb(float[][] px, Func<int, double[]> expected)
    {
        for (var i = 0; i < px.Length; i++)
        {
            var e = expected(i);
            for (var c = 0; c < 3; c++) Assert.AreEqual(e[c], px[i][c], Tolerance, $"pixel{i} ch{c}");
        }
    }

    [TestMethod]
    public void PNGのcICPのDisplayP3指定を解釈する()
    {
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("cICP", [12, 13, 0, 1]));
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.PngCicp, image.Color.Kind);
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.DisplayP3, ColorMath.SrgbToLinear, i));
    }

    [TestMethod]
    public void cICPはiCCPより優先される()
    {
        var p3Icc = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "P3 in iCCP");
        var png = TestImageFiles.InsertPngChunks(RgbaPng(p3Icc), ("cICP", [1, 13, 0, 1]));
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.PngCicp, image.Color.Kind);
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.Srgb, ColorMath.SrgbToLinear, i));
    }

    [TestMethod]
    public void 対応外のcICPは注記付きでsRGBとして扱う()
    {
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("cICP", [9, 18, 0, 1])); // HLG
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.AssumedSrgb, image.Color.Kind);
        StringAssert.Contains(image.Color.Note, "HLG");
    }

    [TestMethod]
    public void PNGのgAMAだけの指定はそのガンマで解釈する()
    {
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("gAMA", TestImageFiles.U32Be(55556))); // 1/1.8
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.PngGamma, image.Color.Kind);
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.Srgb, v => Math.Pow(v, 1 / 0.55556), i));
    }

    [TestMethod]
    public void PNGのgAMAが1_2_2だけならsRGBとして扱う()
    {
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("gAMA", TestImageFiles.U32Be(45455)));
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.PngGamma, image.Color.Kind);
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.Srgb, ColorMath.SrgbToLinear, i));
    }

    [TestMethod]
    public void PNGのcHRMとgAMAから広色域を解釈する()
    {
        // cHRM：白 x,y、赤 x,y、緑 x,y、青 x,y（×100000）
        var chrm = TestImageFiles.U32Be(31270, 32900, 68000, 32000, 26500, 69000, 15000, 6000);
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("gAMA", TestImageFiles.U32Be(45455)), ("cHRM", chrm));
        using var image = Load(png, ".png");

        Assert.AreEqual(SourceColorKind.PngGamma, image.Color.Kind);
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.DisplayP3, v => Math.Pow(v, 1 / 0.45455), i));
    }

    [TestMethod]
    public void PNGのsRGBチャンクはsRGBとして扱う()
    {
        var png = TestImageFiles.InsertPngChunks(RgbaPng(), ("sRGB", [0]));
        using var image = Load(png, ".png");
        Assert.IsTrue(image.Color.Kind is SourceColorKind.PngSrgb or SourceColorKind.ExifSrgb, image.Color.Kind.ToString());
        AssertRgb(Render(image, Acm), i => Expected(ColorMath.Srgb, ColorMath.SrgbToLinear, i));
    }

    // ---- HDR（PQ） ----

    private static double PqEncode(double nits)
    {
        const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
        var y = Math.Pow(nits / 10000, m1);
        return Math.Pow((c1 + c2 * y) / (1 + c3 * y), m2);
    }

    private static LoadedImage LoadPqImage()
    {
        // 16bit RGB：100 nit の灰、1000 nit の白
        double[] nits = [100, 1000];
        var pixels = new byte[nits.Length * 6];
        for (var i = 0; i < nits.Length; i++)
        {
            var code = (ushort)Math.Round(PqEncode(nits[i]) * 65535);
            for (var c = 0; c < 3; c++) BitConverter.TryWriteBytes(pixels.AsSpan(i * 6 + c * 2), code);
        }
        var png = TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format48bppRGB, nits.Length, 1, pixels);
        png = TestImageFiles.InsertPngChunks(png, ("cICP", [9, 16, 0, 1]), ("cLLi", TestImageFiles.U32Be(1000 * 10000, 400 * 10000)));
        return Load(png, ".png");
    }

    [TestMethod]
    public void PNGのPQはHDRとして読みSDR表示ではSDRの白までにトーンマップする()
    {
        using var image = LoadPqImage();
        Assert.AreEqual(SourceColorKind.HdrPq, image.Color.Kind);
        Assert.AreEqual(1000f, image.Color.HdrMaxNits, 0.5f);
        Assert.IsTrue(image.IsHighBitDepth);

        var px = Render(image, DisplayColorInfo.FallbackSrgb);
        Assert.IsTrue(px[1][0] <= 1.02 && px[1][0] > 0.8, $"1000 nit 白 = {px[1][0]}");
        Assert.IsTrue(px[0][0] < px[1][0] && px[0][0] > 0.2, $"100 nit 灰 = {px[0][0]}");
        Assert.AreEqual(px[1][0], px[1][1], 0.01, "無彩色のまま");
    }

    [TestMethod]
    public void PNGのPQはHDR表示ではモニターの最大輝度までを使う()
    {
        using var image = LoadPqImage();
        var hdr = new DisplayColorInfo(DisplayColorMode.Hdr, 240, null, "test HDR", MaxLuminanceNits: 600);
        var px = Render(image, hdr);
        // scRGB 1.0 = 80 nit。100 nit ≒ 1.25、1000 nit はモニター最大 600 nit（7.5）以下へ
        Assert.AreEqual(1.25, px[0][0], 0.2, "100 nit 灰");
        Assert.IsTrue(px[1][0] > 4.5 && px[1][0] <= 7.6, $"1000 nit 白 = {px[1][0]}");
    }

    // ---- EXIF ----

    private static byte[] SmallJpeg(int width = 4, int height = 2)
    {
        var bgr = Enumerable.Repeat((byte)128, width * height * 3).ToArray();
        return TestImageFiles.Encode(Wic, ContainerFormat.Jpeg, PixelFormat.Format24bppBGR, width, height, bgr);
    }

    [TestMethod]
    public void EXIFの未校正とR03はAdobeRGBとして扱う()
    {
        using var image = Load(TestImageFiles.InsertJpegExif(SmallJpeg(), null, 0xFFFF, "R03"), ".jpg");
        Assert.AreEqual(SourceColorKind.ExifAdobeRgb, image.Color.Kind);
    }

    [TestMethod]
    public void EXIFのR98はsRGBのまま()
    {
        using var image = Load(TestImageFiles.InsertJpegExif(SmallJpeg(), null, 0xFFFF, "R98"), ".jpg");
        Assert.AreEqual(SourceColorKind.AssumedSrgb, image.Color.Kind);
    }

    [TestMethod]
    [DataRow((ushort)1, ExifOrientation.Normal)]
    [DataRow((ushort)3, ExifOrientation.Rotate180)]
    [DataRow((ushort)6, ExifOrientation.Rotate90)]
    [DataRow((ushort)8, ExifOrientation.Rotate270)]
    public void EXIFの向きを読み取り縦横を入れ替える(ushort value, ExifOrientation expected)
    {
        using var image = Load(TestImageFiles.InsertJpegExif(SmallJpeg(4, 2), value, 1, null), ".jpg");
        Assert.AreEqual(expected, image.Orientation);
        var swapped = expected.SwapsAxes();
        Assert.AreEqual(swapped ? 2 : 4, image.UprightWidth);
        Assert.AreEqual(swapped ? 4 : 2, image.UprightHeight);
    }

    [TestMethod]
    public void 壊れたメタデータでも例外にならない()
    {
        Assert.IsNull(ColorMetadataReader.ReadPng(new MemoryStream([1, 2, 3])));
        Assert.IsNull(ColorMetadataReader.ReadJpegExif(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE1, 0x00])));
        var truncated = TestImageFiles.InsertJpegExif(SmallJpeg(), 6, 1, null)[..30];
        Assert.IsNull(ColorMetadataReader.ReadJpegExif(new MemoryStream(truncated)));
    }

    [TestMethod]
    public void ICCのparaタグ型3はsRGBの曲線として解釈する()
    {
        // sRGB のパラメトリックカーブ：g=2.4, a=1/1.055, b=0.055/1.055, c=1/12.92, d=0.04045
        static byte[] S15(double v) => TestImageFiles.U32Be((uint)(int)Math.Round(v * 65536));
        var tag = "para"u8.ToArray().Concat(new byte[4]).Concat(new byte[] { 0, 3, 0, 0 })
            .Concat(S15(2.4)).Concat(S15(1 / 1.055)).Concat(S15(0.055 / 1.055)).Concat(S15(1 / 12.92)).Concat(S15(0.04045)).ToArray();
        var f = IccProfileFactory.ParseCurve(tag)!;
        foreach (var v in new[] { 0.0, 0.02, 0.2, 0.5, 1.0 })
        {
            Assert.AreEqual(ColorMath.SrgbToLinear(v), f(v), 1e-4, $"v={v}");
        }
    }

    // ---- 共通 ----

    private static LoadedImage Load(byte[] file, string extension)
    {
        var path = Path.Combine(s_dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, file);
        return WicImageLoader.Load(s_device, path, CancellationToken.None);
    }

    private static float[][] Render(LoadedImage image, DisplayColorInfo display)
    {
        using var ctx = s_device.CreateDeviceContext();
        using var pipeline = ColorPipeline.Create(ctx, Wic, image.Image, image.Color, display);
        return D2DTestRenderer.Draw(ctx, pipeline.Output, image.PixelWidth, image.PixelHeight);
    }

    private static byte[] SystemProfile(string name)
    {
        var path = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color", name);
        if (!File.Exists(path)) Assert.Inconclusive($"{name} がこの環境にありません");
        return File.ReadAllBytes(path);
    }
}
