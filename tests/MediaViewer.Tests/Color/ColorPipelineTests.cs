using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Decoding;
using MediaViewer.Imaging.Graphics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using PixelFormat = Vortice.DCommon.PixelFormat;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace MediaViewer.Tests.Color;

/// <summary>
/// 色変換パイプラインの数値検証。実際の画像ファイル（ICC埋め込みPNG）をデコードし、
/// Direct2D でオフスクリーンの FP16 scRGB へ描画した結果を、独立に計算した期待値と比較する。
/// </summary>
[TestClass]
public sealed class ColorPipelineTests
{
    private const double Tolerance = 0.004;
    private static GraphicsDevice s_device = null!;
    private static string s_dir = null!;

    private static readonly double Gamma22 = IccProfileBuilder.EffectiveGamma(2.2);

    private static readonly DisplayColorInfo AutoColorManaged = new(DisplayColorMode.WideColorGamut, 80, null, "test ACM");

    // 検証用ピクセル（RGBA, 8bit, ストレートアルファ）
    private static readonly byte[][] Pixels =
    [
        [255, 0, 0, 255],
        [0, 255, 0, 255],
        [0, 0, 255, 255],
        [128, 128, 128, 255],
        [255, 255, 255, 255],
        [255, 0, 0, 128],
    ];

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

    [TestMethod]
    public void DisplayP3のICC付き画像は広色域の値のままscRGBへ変換される()
    {
        var icc = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "Test Display P3 g2.2");
        var result = RenderPixels(icc, AutoColorManaged);

        AssertPixels(result, px => Expected(ColorMath.DisplayP3, Gamma22, px, white: 1.0));
        // P3の純赤はsRGB色域外 → scRGBでは R>1、G・B が負になる（広色域が保持されている）
        Assert.IsTrue(result[0][0] > 1.2 && result[0][1] < -0.03 && result[0][2] < -0.01,
            $"P3赤 = ({result[0][0]:F4}, {result[0][1]:F4}, {result[0][2]:F4})");
    }

    [TestMethod]
    public void AdobeRGBのICC付き画像を変換できる()
    {
        var icc = IccProfileBuilder.BuildMatrixTrc(ColorMath.AdobeRgb, 2.2, "Test Adobe RGB");
        var result = RenderPixels(icc, AutoColorManaged);
        AssertPixels(result, px => Expected(ColorMath.AdobeRgb, Gamma22, px, white: 1.0));
    }

    [TestMethod]
    public void ICCのない画像はsRGBとして扱われる()
    {
        var result = RenderPixels(icc: null, AutoColorManaged);
        AssertPixels(result, px => ExpectedSrgb(px, white: 1.0));
    }

    [TestMethod]
    public void HDRではSDR白レベルに合わせて明るさを補正する()
    {
        var hdr = new DisplayColorInfo(DisplayColorMode.Hdr, 240, null, "test HDR");
        var result = RenderPixels(icc: null, hdr);
        AssertPixels(result, px => ExpectedSrgb(px, white: 3.0));
    }

    [TestMethod]
    public void SDRではモニターICCへ変換した値をそのまま出力する()
    {
        // 「モニター」をガンマ2.2のDisplay P3とする。sRGB画像 → P3モニター値 → OSはそれをsRGBとして出力
        var monitorIcc = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "Test monitor P3");
        var sdr = new DisplayColorInfo(DisplayColorMode.Sdr, 80, monitorIcc, "test SDR");
        var result = RenderPixels(icc: null, sdr);

        AssertPixels(result, px =>
        {
            var alpha = px[3] / 255.0;
            var linear = px.Take(3).Select(v => ColorMath.SrgbToLinear(v / 255.0)).ToArray();
            var onMonitor = ColorMath.Convert(ColorMath.Srgb, ColorMath.DisplayP3, linear);
            var encoded = onMonitor.Select(v => Math.Pow(Math.Clamp(v, 0, 1), 1 / Gamma22));
            return [.. encoded.Select(v => ColorMath.SrgbToLinear(v) * alpha), alpha];
        });
    }

    [TestMethod]
    public void モニターICCが取得できないSDRではsRGBモニターとして扱う()
    {
        var result = RenderPixels(icc: null, DisplayColorInfo.FallbackSrgb);
        AssertPixels(result, px => ExpectedSrgb(px, white: 1.0));
    }

    [TestMethod]
    public void 解釈できないICCは落ちずにsRGBへフォールバックする()
    {
        // ヘッダーだけ正しく、タグが壊れているプロファイル
        var broken = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "broken");
        Array.Fill(broken, (byte)0xEE, 132, broken.Length - 132);

        using var ctx = s_device.CreateDeviceContext();
        using var bmp = CreateSolidBitmap(ctx, [255, 0, 0, 255]);
        using var pipeline = ColorPipeline.Create(ctx, s_device.WicFactory, bmp, new SourceColor(SourceColorKind.EmbeddedIcc, broken), AutoColorManaged);

        Assert.IsTrue(pipeline.SourceFellBack);
        var px = Draw(ctx, pipeline.Output, 1, 1)[0];
        Assert.AreEqual(1.0, px[0], Tolerance);
        Assert.AreEqual(0.0, px[1], Tolerance);
    }

    [TestMethod]
    public void RGB以外やヘッダー不正のICCは受け付けない()
    {
        var icc = IccProfileBuilder.BuildMatrixTrc(ColorMath.Srgb, 2.2, "x");
        Assert.IsTrue(SourceColor.IsSupportedRgbIcc(icc, out _));

        var gray = (byte[])icc.Clone();
        "GRAY"u8.CopyTo(gray.AsSpan(16));
        Assert.IsFalse(SourceColor.IsSupportedRgbIcc(gray, out _));

        var badSignature = (byte[])icc.Clone();
        badSignature[36] = 0;
        Assert.IsFalse(SourceColor.IsSupportedRgbIcc(badSignature, out _));

        Assert.IsFalse(SourceColor.IsSupportedRgbIcc(icc.AsSpan(0, 100), out _));
    }

    // ---- 期待値 ----

    private static double[] Expected(ColorMath.RgbSpace space, double gamma, byte[] px, double white)
    {
        var alpha = px[3] / 255.0;
        var linear = px.Take(3).Select(v => Math.Pow(v / 255.0, gamma)).ToArray();
        var srgbLinear = ColorMath.Convert(space, ColorMath.Srgb, linear);
        return [.. srgbLinear.Select(v => v * white * alpha), alpha];
    }

    private static double[] ExpectedSrgb(byte[] px, double white)
    {
        var alpha = px[3] / 255.0;
        return [.. px.Take(3).Select(v => ColorMath.SrgbToLinear(v / 255.0) * white * alpha), alpha];
    }

    private static void AssertPixels(float[][] actual, Func<byte[], double[]> expected)
    {
        for (var i = 0; i < Pixels.Length; i++)
        {
            var exp = expected(Pixels[i]);
            for (var c = 0; c < 4; c++)
            {
                Assert.AreEqual(exp[c], actual[i][c], Tolerance,
                    $"pixel#{i} ({string.Join(",", Pixels[i])}) ch{c}: expected {exp[c]:F4}, actual {actual[i][c]:F4}");
            }
        }
    }

    // ---- 画像の生成と描画 ----

    private static float[][] RenderPixels(byte[]? icc, DisplayColorInfo display)
    {
        var path = WritePng(icc);
        using var image = WicImageLoader.Load(s_device, path, CancellationToken.None);
        if (icc is null)
        {
            // WIC の PNG エンコーダーは sRGB チャンクを書くため、PNG sRGB と判定されることもある
            Assert.IsTrue(image.Color.Kind is SourceColorKind.AssumedSrgb or SourceColorKind.PngSrgb or SourceColorKind.ExifSrgb, image.Color.Kind.ToString());
        }
        else
        {
            Assert.AreEqual(SourceColorKind.EmbeddedIcc, image.Color.Kind);
        }

        using var ctx = s_device.CreateDeviceContext();
        ctx.RenderingControls = new RenderingControls { BufferPrecision = BufferPrecision.PerChannel32Float };
        using var pipeline = ColorPipeline.Create(ctx, s_device.WicFactory, image.Image, image.Color, display);
        return Draw(ctx, pipeline.Output, Pixels.Length, 1);
    }

    private static string WritePng(byte[]? icc)
    {
        var wic = s_device.WicFactory;
        var path = Path.Combine(s_dir, Guid.NewGuid().ToString("N") + ".png");
        var bgra = new byte[Pixels.Length * 4];
        for (var i = 0; i < Pixels.Length; i++)
        {
            bgra[i * 4 + 0] = Pixels[i][2];
            bgra[i * 4 + 1] = Pixels[i][1];
            bgra[i * 4 + 2] = Pixels[i][0];
            bgra[i * 4 + 3] = Pixels[i][3];
        }
        using (var fs = File.Create(path))
        using (var encoder = wic.CreateEncoder(ContainerFormat.Png, fs, BitmapEncoderCacheOption.NoCache))
        {
            using var frame = encoder.CreateNewFrame(out var props);
            frame.Initialize(props);
            frame.SetSize((uint)Pixels.Length, 1);
            frame.SetPixelFormat(Vortice.WIC.PixelFormat.Format32bppBGRA);
            if (icc is not null)
            {
                using var cc = wic.CreateColorContext();
                cc.InitializeFromMemory(icc, (uint)icc.Length).CheckError();
                frame.SetColorContexts([cc]);
            }
            frame.WritePixels(1, (uint)(Pixels.Length * 4), bgra);
            frame.Commit();
            encoder.Commit();
            props?.Dispose();
        }
        return path;
    }

    private static ID2D1Bitmap1 CreateSolidBitmap(ID2D1DeviceContext5 ctx, byte[] rgba)
    {
        var bgra = new byte[] { rgba[2], rgba[1], rgba[0], rgba[3] };
        unsafe
        {
            fixed (byte* p = bgra)
            {
                return ctx.CreateBitmap(new SizeI(1, 1), (IntPtr)p, 4,
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
            }
        }
    }

    private static float[][] Draw(ID2D1DeviceContext5 ctx, ID2D1Image image, int width, int height)
    {
        var fmt = new PixelFormat(Format.R16G16B16A16_Float, AlphaMode.Premultiplied);
        var size = new SizeI(width, height);
        using var target = ctx.CreateBitmap(size, IntPtr.Zero, 0, new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target));
        using var readback = ctx.CreateBitmap(size, IntPtr.Zero, 0, new BitmapProperties1(fmt, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        ctx.DrawImage(image, null, null, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        ctx.EndDraw().CheckError();
        ctx.Target = null;

        readback.CopyFromBitmap(target);
        var map = readback.Map(MapOptions.Read);
        try
        {
            var result = new float[width * height][];
            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    var row = (Half*)((byte*)map.Bits + y * map.Pitch);
                    for (var x = 0; x < width; x++)
                    {
                        result[y * width + x] = [(float)row[x * 4], (float)row[x * 4 + 1], (float)row[x * 4 + 2], (float)row[x * 4 + 3]];
                    }
                }
            }
            return result;
        }
        finally
        {
            readback.Unmap();
        }
    }
}
