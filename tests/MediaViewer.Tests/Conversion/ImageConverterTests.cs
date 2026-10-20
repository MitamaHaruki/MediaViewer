using MediaViewer.Core.Conversion;
using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Conversion;
using MediaViewer.Imaging.Decoding;
using MediaViewer.Imaging.Graphics;
using MediaViewer.Tests.Color;
using SharpGen.Runtime.Win32;
using Vortice.WIC;

namespace MediaViewer.Tests.Conversion;

[TestClass]
public sealed class ImageConverterTests
{
    private static GraphicsDevice s_device = null!;
    private static string s_dir = null!;
    private static IWICImagingFactory Wic => s_device.WicFactory;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_device = GraphicsDevice.Create();
        s_dir = Path.Combine(Path.GetTempPath(), "MediaViewerTests", "img-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(s_dir);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        s_device.Dispose();
        try { Directory.Delete(s_dir, true); } catch (IOException) { }
    }

    // ---- ヘルパー ----

    private static string Save(byte[] file, string ext)
    {
        var path = Path.Combine(s_dir, Guid.NewGuid().ToString("N")[..8] + ext);
        File.WriteAllBytes(path, file);
        return path;
    }

    private static async Task<(string Output, ImageConversionPreparation Prep)> Convert(string input, ConversionFormat target)
    {
        var prep = ImageConverter.Prepare(input, target, CancellationToken.None);
        var output = ConversionPaths.Suggest(input, target);
        await ImageConverter.ExecuteAsync(prep, output, overwrite: true, FfmpegTools.Locate(), null, CancellationToken.None);
        Assert.IsTrue(File.Exists(output));
        Assert.AreEqual(0, Directory.GetFiles(s_dir, $"~{Path.GetFileNameWithoutExtension(output)}.converting-*").Length, "一時ファイルが残らない");
        return (output, prep);
    }

    /// <summary>出力を BGRA（ストレートアルファ、8bit）で読み戻す。</summary>
    private static (int W, int H, byte[] Bgra) ReadPixels(string path)
    {
        using var decoder = Wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        using var converter = Wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var (w, h) = (converter.Size.Width, converter.Size.Height);
        var pixels = new byte[w * h * 4];
        converter.CopyPixels((uint)(w * 4), pixels);
        return (w, h, pixels);
    }

    private static byte[] Bgra(params (byte R, byte G, byte B, byte A)[] px) =>
        px.SelectMany(p => new[] { p.B, p.G, p.R, p.A }).ToArray();

    // ---- ICC ----

    [TestMethod]
    public async Task ICCはJPEG_TIFF_PNGへ埋め込んで引き継ぐ()
    {
        var p3 = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "P3 test");
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 2, 1,
            Bgra((200, 40, 30, 255), (30, 200, 40, 255)), p3), ".png");

        foreach (var target in new[] { ConversionFormat.Jpeg, ConversionFormat.Tiff })
        {
            var (output, prep) = await Convert(input, target);
            Assert.IsFalse(prep.Notices.Any(n => n.Contains("sRGB に変換")), target.ToString());
            using var img = WicImageLoader.Load(s_device, output, CancellationToken.None);
            Assert.AreEqual(SourceColorKind.EmbeddedIcc, img.Color.Kind, target.ToString());
            CollectionAssert.AreEqual(p3, img.Color.IccProfile, $"{target}：同じICCを保持する");
        }
    }

    [TestMethod]
    public async Task ICCを保存できないBMPではsRGBの画素に変換して通知する()
    {
        var p3 = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "P3 test");
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 1, 1, Bgra((128, 128, 64, 255)), p3), ".png");

        var (output, prep) = await Convert(input, ConversionFormat.Bmp);
        Assert.IsTrue(prep.Notices.Any(n => n.Contains("sRGB に変換")));

        // 期待値：P3（ガンマ2.2）→ sRGB（sRGB の符号化）
        double[] linear = [Math.Pow(128 / 255.0, 563 / 256.0), Math.Pow(128 / 255.0, 563 / 256.0), Math.Pow(64 / 255.0, 563 / 256.0)];
        var srgb = ColorMath.Convert(ColorMath.DisplayP3, ColorMath.Srgb, linear)
            .Select(v => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055).Select(v => Math.Clamp(v, 0, 1) * 255).ToArray();
        var (_, _, px) = ReadPixels(output);
        Assert.AreEqual(srgb[0], px[2], 3, "R");
        Assert.AreEqual(srgb[1], px[1], 3, "G");
        Assert.AreEqual(srgb[2], px[0], 3, "B");
    }

    // ---- 透過 ----

    private static string AlphaPng() => Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 3, 1,
        Bgra((255, 0, 0, 255), (0, 0, 255, 0), (0, 0, 0, 128))), ".png");

    [TestMethod]
    public async Task 透過非対応のJPEGでは白で合成し事前に通知する()
    {
        // JPEG は色差を周囲と平均するため、各色を 16px のブロックにする（赤・透明・半透明の黒）
        const int cell = 16;
        (byte R, byte G, byte B, byte A)[] colors = [(255, 0, 0, 255), (0, 0, 255, 0), (0, 0, 0, 128)];
        var pixels = new byte[3 * cell * cell * 4];
        for (var y = 0; y < cell; y++)
            for (var x = 0; x < 3 * cell; x++)
            {
                var c = colors[x / cell];
                var i = (y * 3 * cell + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, c.A);
            }
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 3 * cell, cell, pixels), ".png");

        var (output, prep) = await Convert(input, ConversionFormat.Jpeg);
        Assert.IsTrue(prep.HasTransparency);
        Assert.IsTrue(prep.Notices.Any(n => n.Contains("白で塗りつぶします")));
        var (w, _, px) = ReadPixels(output);
        int At(int cellIndex) => ((cell / 2) * w + cellIndex * cell + cell / 2) * 4;
        Assert.IsTrue(px[At(0) + 2] > 230 && px[At(0)] < 30, "不透明な赤は赤のまま");
        Assert.IsTrue(px.AsSpan(At(1), 3).ToArray().All(v => v > 245), "透明な部分は白");
        Assert.IsTrue(px.AsSpan(At(2), 3).ToArray().All(v => v is > 115 and < 140), "半透明の黒は白と混ざって灰色");
    }

    [TestMethod]
    public async Task GIFは透明を保ち_減色と半透明について通知する()
    {
        var (output, prep) = await Convert(AlphaPng(), ConversionFormat.Gif);
        Assert.IsTrue(prep.Notices.Any(n => n.Contains("256 色")));
        Assert.IsTrue(prep.Notices.Any(n => n.Contains("半透明")));
        var (_, _, px) = ReadPixels(output);
        Assert.AreEqual(255, px[3], "不透明");
        Assert.AreEqual(0, px[7], "透明は透明のまま");
    }

    [TestMethod]
    public async Task 透過のない画像では透過の通知を出さない()
    {
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 1, 1, Bgra((1, 2, 3, 255))), ".png");
        var (_, prep) = await Convert(input, ConversionFormat.Jpeg);
        Assert.IsFalse(prep.HasTransparency);
        Assert.IsFalse(prep.Notices.Any(n => n.Contains("透明")));
    }

    // ---- EXIF の向き ----

    [TestMethod]
    [DataRow(ExifOrientation.FlipHorizontal)]
    [DataRow(ExifOrientation.Rotate180)]
    [DataRow(ExifOrientation.FlipVertical)]
    [DataRow(ExifOrientation.Transpose)]
    [DataRow(ExifOrientation.Rotate90)]
    [DataRow(ExifOrientation.Transverse)]
    [DataRow(ExifOrientation.Rotate270)]
    public async Task EXIFの向きを画素に反映し表示と同じ向きで保存する(ExifOrientation orientation)
    {
        // 3x2 マスの各色ブロック（JPEG の劣化に耐えるよう 1マス 16px）
        const int cell = 16;
        (byte R, byte G, byte B)[,] colors = { { (255, 0, 0), (0, 255, 0), (0, 0, 255) }, { (255, 255, 0), (0, 0, 0), (255, 255, 255) } };
        var bgr = new byte[3 * cell * 2 * cell * 3];
        for (var y = 0; y < 2 * cell; y++)
            for (var x = 0; x < 3 * cell; x++)
            {
                var c = colors[y / cell, x / cell];
                var i = (y * 3 * cell + x) * 3;
                (bgr[i], bgr[i + 1], bgr[i + 2]) = (c.B, c.G, c.R);
            }
        var jpeg = TestImageFiles.Encode(Wic, ContainerFormat.Jpeg, PixelFormat.Format24bppBGR, 3 * cell, 2 * cell, bgr);
        var input = Save(TestImageFiles.InsertJpegExif(jpeg, (ushort)orientation, 1, null), ".jpg");

        var (output, prep) = await Convert(input, ConversionFormat.Png);
        var (w, h, px) = ReadPixels(output);
        var m = orientation.ToUprightMatrix(3 * cell, 2 * cell);
        Assert.AreEqual(orientation.SwapsAxes() ? 2 * cell : 3 * cell, w);

        // 元の各マスの中心が、表示用の変換（ビューワーと同じ行列）の行き先で同じ色になっている
        for (var cy = 0; cy < 2; cy++)
            for (var cx = 0; cx < 3; cx++)
            {
                var p = System.Numerics.Vector2.Transform(new((cx + 0.5f) * cell, (cy + 0.5f) * cell), m);
                var i = ((int)p.Y * w + (int)p.X) * 4;
                var c = colors[cy, cx];
                Assert.AreEqual(c.R, px[i + 2], 40, $"{orientation} マス({cx},{cy}) R");
                Assert.AreEqual(c.G, px[i + 1], 40, $"{orientation} マス({cx},{cy}) G");
                Assert.AreEqual(c.B, px[i], 40, $"{orientation} マス({cx},{cy}) B");
            }
        using var img = WicImageLoader.Load(s_device, output, CancellationToken.None);
        Assert.AreEqual(ExifOrientation.Normal, img.Orientation, "向き情報は書かない（二重適用を防ぐ）");
    }

    // ---- CMYK ----

    [TestMethod]
    public async Task CMYKはJPEGとTIFFでは保持し_PNGではDisplayP3に変換する()
    {
        var swop = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color", "RSWOP.icm");
        if (!File.Exists(swop)) Assert.Inconclusive("RSWOP.icm がありません");
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Tiff, PixelFormat.Format32bppCMYK, 2, 1,
            [0, 0, 0, 0, 255, 0, 0, 0], File.ReadAllBytes(swop)), ".tif");

        var (jpeg, _) = await Convert(input, ConversionFormat.Jpeg);
        using (var decoder = Wic.CreateDecoderFromFileName(jpeg, FileAccess.Read, DecodeOptions.CacheOnDemand))
        using (var frame = decoder.GetFrame(0))
        {
            Assert.AreEqual(PixelFormat.Format32bppCMYK, frame.PixelFormat, "JPEG は CMYK のまま");
        }

        var (png, prep) = await Convert(input, ConversionFormat.Png);
        Assert.IsTrue(prep.Notices.Any(n => n.Contains("Display P3")));
        using var img = WicImageLoader.Load(s_device, png, CancellationToken.None);
        Assert.AreEqual(SourceColorKind.EmbeddedIcc, img.Color.Kind);
        CollectionAssert.AreEqual(IccProfileFactory.DisplayP3, img.Color.IccProfile);
    }

    // ---- WebP ----

    /// <summary>1×1 の可逆 WebP（赤）。WIC で WebP を読めるか確かめるのに使う。</summary>
    private const string TinyWebP = "UklGRhwAAABXRUJQVlA4TA8AAAAvAAAAAAcQ/Y/+ByKi/wEA";

    /// <summary>
    /// WebP の変換には FFmpeg、変換後の確認（読み戻し）には WIC の WebP デコーダーが要る。
    /// Windows 11 は標準で WebP を読めるが、Windows Server（GitHub Actions など）には無いことがある。
    /// </summary>
    private static void RequireWebPSupport()
    {
        if (FfmpegTools.Locate() is null) Assert.Inconclusive("FFmpeg が見つかりません");
        try
        {
            ReadPixels(Save(System.Convert.FromBase64String(TinyWebP), ".webp"));
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or InvalidDataException)
        {
            Assert.Inconclusive($"この環境の WIC は WebP を読めません（{e.Message}）");
        }
    }

    [TestMethod]
    public async Task WebPにICCを埋め込む()
    {
        RequireWebPSupport();
        var p3 = IccProfileBuilder.BuildMatrixTrc(ColorMath.DisplayP3, 2.2, "P3 test");
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 4, 3,
            Enumerable.Repeat(Bgra((10, 200, 30, 255)), 12).SelectMany(x => x).ToArray(), p3), ".png");

        var (output, prep) = await Convert(input, ConversionFormat.WebP);
        CollectionAssert.AreEqual(p3, WebPContainer.ReadIccProfile(File.ReadAllBytes(output)), "ICCP チャンク");
        Assert.IsFalse(prep.Notices.Any(n => n.Contains("劣化")), "PNG（可逆）からは可逆の WebP にする");
        var (w, h, px) = ReadPixels(output);
        Assert.AreEqual((4, 3), (w, h));
        Assert.AreEqual(200, px[1], 2, "可逆なので値が保たれる");
        using var img = WicImageLoader.Load(s_device, output, CancellationToken.None);
        Assert.AreEqual(SourceColorKind.EmbeddedIcc, img.Color.Kind, "ビューワーで WebP の ICC を色管理に使える");
    }

    [TestMethod]
    public async Task 透過のあるWebPは透過を保つ()
    {
        RequireWebPSupport();
        var (output, _) = await Convert(AlphaPng(), ConversionFormat.WebP);
        var (_, _, px) = ReadPixels(output);
        Assert.AreEqual(255, px[3]);
        Assert.AreEqual(0, px[7]);
    }

    // ---- メタデータ・安全性 ----

    [TestMethod]
    public async Task カメラ情報のメタデータを引き継ぐ()
    {
        var jpegPath = Path.Combine(s_dir, "meta.jpg");
        using (var fs = File.Create(jpegPath))
        using (var encoder = Wic.CreateEncoder(ContainerFormat.Jpeg, fs, BitmapEncoderCacheOption.NoCache))
        {
            using var frame = encoder.CreateNewFrame(out var props);
            frame.Initialize(props);
            frame.SetSize(8, 8);
            frame.SetPixelFormat(PixelFormat.Format24bppBGR);
            using (var writer = frame.MetadataQueryWriter)
            {
                writer.SetMetadataByName("System.Photo.CameraModel", new Variant { Value = "MediaViewer TestCam" });
            }
            frame.WritePixels(8, 24, new byte[8 * 24]);
            frame.Commit();
            encoder.Commit();
            props?.Dispose();
        }

        foreach (var target in new[] { ConversionFormat.Tiff, ConversionFormat.Png })
        {
            var (output, _) = await Convert(jpegPath, target);
            using var decoder = Wic.CreateDecoderFromFileName(output, FileAccess.Read, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            using var reader = frame.MetadataQueryReader;
            string? model = null;
            try { model = reader.GetMetadataByName("System.Photo.CameraModel").Value as string; } catch (SharpGen.Runtime.SharpGenException) { }
            if (target == ConversionFormat.Tiff) Assert.AreEqual("MediaViewer TestCam", model, "TIFF はカメラ情報を保持する");
            else Console.WriteLine($"PNG の CameraModel: {model ?? "（保持されない）"}");
        }
    }

    [TestMethod]
    public async Task 元ファイルへの上書きは拒否する()
    {
        var input = Save(TestImageFiles.Encode(Wic, ContainerFormat.Png, PixelFormat.Format32bppBGRA, 1, 1, Bgra((1, 2, 3, 255))), ".png");
        var prep = ImageConverter.Prepare(input, ConversionFormat.Jpeg, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ImageConverter.ExecuteAsync(prep, input, overwrite: true, null, null, CancellationToken.None));
    }
}
