using MediaViewer.Core.Conversion;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Decoding;
using SharpGen.Runtime;
using Vortice.WIC;
using PixelFormat = Vortice.WIC.PixelFormat;

namespace MediaViewer.Imaging.Conversion;

/// <summary>変換前の調査結果。Notices は変換開始前に利用者へ見せる。</summary>
public sealed record ImageConversionPreparation(
    string InputPath,
    ConversionFormat Target,
    int OutputWidth,
    int OutputHeight,
    bool HasTransparency,
    IReadOnlyList<string> Notices);

/// <summary>
/// 画像の形式変換（仕様 §9.2「画像 → 画像」、§5.2-8）。
/// <list type="bullet">
/// <item>元の解像度を保ち、EXIF の向きは画素に反映して保存する（向き情報との二重適用を避ける）。</item>
/// <item>ICC は保存先が扱えれば埋め込む。扱えない形式（BMP・GIF）は sRGB へ変換して通知する。</item>
/// <item>透過に対応しない形式は白で合成し、事前に通知する。アニメーション・複数ページは最初のフレームのみ。</item>
/// <item>一時ファイルへ書き、読み戻して確認してから最終名に置き換える。元ファイルは上書きしない。</item>
/// </list>
/// WebP の書き出しは FFmpeg（libwebp）を使う（WIC には WebP のエンコーダーがないため）。
/// </summary>
public static class ImageConverter
{
    private const float JpegQuality = 0.92f;
    private const int WebPLossyQuality = 90;
    private const int WebPMaxDimension = 16383;
    private const int StripBytes = 4 * 1024 * 1024;

    private static readonly string[] MetadataPolicies =
    [
        "System.Photo.DateTaken", "System.Photo.CameraManufacturer", "System.Photo.CameraModel",
        "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
        "System.Photo.LensModel", "System.Author", "System.Copyright", "System.Title", "System.Subject",
        "System.Comment", "System.Keywords", "System.ApplicationName",
    ];

    public static bool CanHoldIcc(ConversionFormat f) => f is ConversionFormat.Png or ConversionFormat.Jpeg or ConversionFormat.Tiff or ConversionFormat.WebP;

    private static bool CanHoldAlpha(ConversionFormat f) => f is ConversionFormat.Png or ConversionFormat.Tiff or ConversionFormat.WebP or ConversionFormat.Gif;

    private static bool CanHoldHighBitDepth(ConversionFormat f) => f is ConversionFormat.Png or ConversionFormat.Tiff;

    private static bool CanHoldMetadata(ConversionFormat f) => f is ConversionFormat.Png or ConversionFormat.Jpeg or ConversionFormat.Tiff;

    public static ImageConversionPreparation Prepare(string inputPath, ConversionFormat target, CancellationToken cancellationToken)
    {
        using var wic = new IWICImagingFactory();
        using var decoder = wic.CreateDecoderFromFileName(inputPath, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var info = Inspect(wic, decoder, frame, inputPath);
        if (info.Color.IsHdr) throw new NotSupportedException("HDR 画像の変換は未対応です。");

        var notices = new List<string>();
        if (decoder.FrameCount > 1)
            notices.Add($"複数フレーム（アニメーション・複数ページ、{decoder.FrameCount} フレーム）のうち、最初のフレームだけを変換します。");

        var hasTransparency = info.HasAlphaChannel && ScanTransparency(wic, frame, cancellationToken);
        if (hasTransparency && !CanHoldAlpha(target))
            notices.Add("この形式は透過を扱えないため、透明な部分を白で塗りつぶします（元に戻せない変更です）。");
        if (hasTransparency && target == ConversionFormat.Gif)
            notices.Add("GIF は半透明を表現できないため、各画素を透明か不透明のどちらかにします。");
        if (target == ConversionFormat.Gif)
            notices.Add("GIF は 256 色までのため、色数を減らします（色の段差が出る場合があります）。");
        if (target == ConversionFormat.Jpeg)
            notices.Add("JPEG は非可逆圧縮のため、画質がわずかに劣化します（品質 92 で保存）。");
        if (target == ConversionFormat.WebP && !IsLosslessSource(inputPath))
            notices.Add("WebP の非可逆圧縮（品質 90）で保存するため、画質がわずかに劣化します。");

        if (info.IsCmyk && target is not (ConversionFormat.Jpeg or ConversionFormat.Tiff))
            notices.Add(CanHoldIcc(target)
                ? "CMYK 画像を RGB（Display P3）に変換して保存します。"
                : "CMYK 画像を RGB（sRGB）に変換して保存します。sRGB の範囲外の色は近い色になります。");
        else if (OutputIcc(info) is not null && !CanHoldIcc(target))
            notices.Add("この形式は ICC プロファイルを保存できないため、sRGB に変換します。広色域の色は sRGB の範囲に収まります。");

        if (info.IsHighBitDepth && !CanHoldHighBitDepth(target))
            notices.Add("16bit の階調を 8bit に減らして保存します。");
        if (info.HasMetadata && !CanHoldMetadata(target))
            notices.Add("撮影日時やカメラ情報などのメタデータは、この形式では保存できません。");
        if (info.Orientation != ExifOrientation.Normal)
            notices.Add("EXIF の向き情報に合わせて画素を回転して保存します（どのソフトでも同じ向きで表示されます）。");

        var (w, h) = info.Orientation.SwapsAxes() ? (info.Height, info.Width) : (info.Width, info.Height);
        if (target == ConversionFormat.WebP && Math.Max(w, h) > WebPMaxDimension)
            throw new NotSupportedException($"WebP は {WebPMaxDimension} ピクセルを超える画像を保存できません。");
        return new ImageConversionPreparation(inputPath, target, w, h, hasTransparency, notices);
    }

    public static async Task ExecuteAsync(ImageConversionPreparation prep, string outputPath, bool overwrite, FfmpegTools? tools,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (ConversionPaths.IsSameFile(prep.InputPath, outputPath))
            throw new InvalidOperationException("元のファイルは上書きできません。別の名前か保存先を指定してください。");
        if (!overwrite && File.Exists(outputPath))
            throw new IOException("同じ名前のファイルが既にあります。");
        if (prep.Target == ConversionFormat.WebP && tools is null)
            throw new NotSupportedException("WebP の保存に必要な FFmpeg が見つかりません。");

        var temp = ConversionPaths.TemporaryFor(outputPath);
        try
        {
            if (prep.Target == ConversionFormat.WebP)
            {
                await EncodeWebPAsync(prep, temp, tools!, progress, cancellationToken);
            }
            else
            {
                await Task.Run(() => EncodeWithWic(prep, temp, progress, cancellationToken), cancellationToken);
            }
            Verify(temp, prep);
            File.Move(temp, outputPath, overwrite);
            AppLog.Info($"画像変換完了：{prep.Target}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Error($"画像変換失敗：{prep.Target}", e);
            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    // ---- 調査 ----

    private sealed record SourceInfo(int Width, int Height, Guid PixelFormat, bool IsCmyk, bool IsHighBitDepth, bool HasAlphaChannel,
        SourceColor Color, byte[]? CmykProfile, ExifOrientation Orientation, bool HasMetadata);

    private static SourceInfo Inspect(IWICImagingFactory wic, IWICBitmapDecoder decoder, IWICBitmapFrameDecode frame, string path)
    {
        var format = frame.PixelFormat;
        var isCmyk = WicImageLoader.CmykFormats.Contains(format);
        var (color, cmyk) = WicImageLoader.ReadSourceColor(wic, frame, decoder.ContainerFormat,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), isCmyk);
        return new SourceInfo(frame.Size.Width, frame.Size.Height, format, isCmyk, WicImageLoader.IsHighBitDepth(wic, format),
            HasAlphaChannel(wic, format), color, cmyk, WicImageLoader.ReadOrientation(frame), HasMetadata(frame));
    }

    private static bool HasAlphaChannel(IWICImagingFactory wic, Guid format)
    {
        try
        {
            using var info = wic.CreateComponentInfo(format);
            using var pf = info.QueryInterface<IWICPixelFormatInfo2>();
            return pf.SupportsTransparency();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>実際に不透明でない画素があるかを調べる（アルファチャンネルがあっても全て不透明なことが多い）。</summary>
    private static bool ScanTransparency(IWICImagingFactory wic, IWICBitmapSource frame, CancellationToken cancellationToken)
    {
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var (w, h) = (converter.Size.Width, converter.Size.Height);
        var stride = w * 4;
        var rows = Math.Clamp(StripBytes / stride, 1, h);
        var buffer = new byte[stride * rows];
        for (var y = 0; y < h; y += rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(rows, h - y);
            var span = buffer.AsSpan(0, stride * n);
            converter.CopyPixels(new Vortice.Mathematics.RectI(0, y, w, n), (uint)stride, span);
            for (var i = 3; i < span.Length; i += 4)
            {
                if (span[i] != 255) return true;
            }
        }
        return false;
    }

    private static bool HasMetadata(IWICBitmapFrameDecode frame)
    {
        try
        {
            using var reader = frame.MetadataQueryReader;
            if (reader is null) return false;
            foreach (var name in MetadataPolicies)
            {
                try
                {
                    if (reader.GetMetadataByName(name).Value is not null) return true;
                }
                catch (SharpGenException)
                {
                }
            }
        }
        catch (SharpGenException)
        {
        }
        return false;
    }

    private static bool IsLosslessSource(string path) => ConversionFormats.FromPath(path) is
        ConversionFormat.Png or ConversionFormat.Bmp or ConversionFormat.Gif or ConversionFormat.Tiff;

    /// <summary>出力に埋め込むべき ICC（sRGB 相当なら null）。</summary>
    private static byte[]? OutputIcc(SourceInfo info) => info.Color.Kind switch
    {
        SourceColorKind.ExifAdobeRgb => IccProfileFactory.AdobeRgb,
        SourceColorKind.InvalidIccFallbackSrgb => null,
        _ => info.Color.IccProfile,
    };

    // ---- 画素の準備（色・向き） ----

    /// <summary>
    /// 出力用の画素ソースを組み立てる。戻り値の Owned は呼び出し側で破棄する。
    /// </summary>
    private static (IWICBitmapSource Source, byte[]? Icc, bool KeepCmyk, List<IDisposable> Owned) BuildSource(
        IWICImagingFactory wic, IWICBitmapFrameDecode frame, SourceInfo info, ConversionFormat target)
    {
        var owned = new List<IDisposable>();
        IWICBitmapSource source = frame;
        byte[]? icc;
        var keepCmyk = info.IsCmyk && target is ConversionFormat.Jpeg or ConversionFormat.Tiff;

        if (info.IsCmyk && !keepCmyk)
        {
            var from = info.CmykProfile ?? WicImageLoader.DefaultCmykProfile()
                ?? throw new NotSupportedException("CMYKプロファイルが見つからないため変換できません。");
            icc = CanHoldIcc(target) ? IccProfileFactory.DisplayP3 : null;
            source = Transform(wic, source, from, icc ?? IccProfileFactory.Srgb, info.IsHighBitDepth, owned);
        }
        else if (keepCmyk)
        {
            icc = info.CmykProfile;
        }
        else
        {
            icc = OutputIcc(info);
            if (icc is not null && !CanHoldIcc(target))
            {
                // ICC を保存できない形式：sRGB の画素に変換する（仕様 §5.2-8）
                source = Transform(wic, source, icc, IccProfileFactory.Srgb, false, owned);
                icc = null;
            }
        }

        if (info.Orientation != ExifOrientation.Normal)
        {
            var rotator = wic.CreateBitmapFlipRotator();
            owned.Add(rotator);
            rotator.Initialize(source, ToWicTransform(info.Orientation));
            source = rotator;
        }
        return (source, icc, keepCmyk, owned);
    }

    private static IWICBitmapSource Transform(IWICImagingFactory wic, IWICBitmapSource source, byte[] from, byte[] to, bool highBitDepth, List<IDisposable> owned)
    {
        var src = wic.CreateColorContext();
        owned.Add(src);
        src.InitializeFromMemory(from, (uint)from.Length).CheckError();
        var dst = wic.CreateColorContext();
        owned.Add(dst);
        dst.InitializeFromMemory(to, (uint)to.Length).CheckError();
        var transform = wic.CreateColorTransformer();
        owned.Add(transform);
        transform.Initialize(source, src, dst, highBitDepth ? PixelFormat.Format64bppRGBA : PixelFormat.Format32bppBGRA);
        return transform;
    }

    /// <summary>EXIF の向き → WIC の回転・反転（WIC は反転してから回転する）。</summary>
    internal static BitmapTransformOptions ToWicTransform(ExifOrientation o) => o switch
    {
        ExifOrientation.FlipHorizontal => BitmapTransformOptions.FlipHorizontal,
        ExifOrientation.Rotate180 => BitmapTransformOptions.Rotate180,
        ExifOrientation.FlipVertical => BitmapTransformOptions.FlipVertical,
        ExifOrientation.Transpose => BitmapTransformOptions.Rotate270 | BitmapTransformOptions.FlipHorizontal,
        ExifOrientation.Rotate90 => BitmapTransformOptions.Rotate90,
        ExifOrientation.Transverse => BitmapTransformOptions.Rotate90 | BitmapTransformOptions.FlipHorizontal,
        ExifOrientation.Rotate270 => BitmapTransformOptions.Rotate270,
        _ => BitmapTransformOptions.Rotate0,
    };

    // ---- WIC での書き出し ----

    private static void EncodeWithWic(ImageConversionPreparation prep, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var wic = new IWICImagingFactory();
        using var decoder = wic.CreateDecoderFromFileName(prep.InputPath, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var info = Inspect(wic, decoder, frame, prep.InputPath);
        var (source, icc, keepCmyk, owned) = BuildSource(wic, frame, info, prep.Target);
        try
        {
            var target = prep.Target;
            var high = info.IsHighBitDepth && CanHoldHighBitDepth(target) && !keepCmyk;
            var alpha = prep.HasTransparency && CanHoldAlpha(target);
            if (prep.HasTransparency && !CanHoldAlpha(target))
            {
                source = CompositeOnWhite(wic, source, owned, cancellationToken);
            }
            var format = keepCmyk
                ? PixelFormat.Format32bppCMYK
                : target switch
                {
                    ConversionFormat.Gif => PixelFormat.Format8bppIndexed,
                    ConversionFormat.Png or ConversionFormat.Tiff => high
                        ? (alpha ? PixelFormat.Format64bppRGBA : PixelFormat.Format48bppRGB)
                        : (alpha ? PixelFormat.Format32bppBGRA : PixelFormat.Format24bppBGR),
                    _ => PixelFormat.Format24bppBGR,
                };

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var encoder = wic.CreateEncoder(Container(target), stream, BitmapEncoderCacheOption.NoCache);
            using var frameEncode = encoder.CreateNewFrame(out var props);
            if (target == ConversionFormat.Jpeg) props.Set("ImageQuality", JpegQuality);
            if (target == ConversionFormat.Tiff) props.Set("TiffCompressionMethod", (byte)4); // LZW（可逆）
            frameEncode.Initialize(props);
            props.Dispose();
            frameEncode.SetSize((uint)prep.OutputWidth, (uint)prep.OutputHeight);
            var actual = format;
            frameEncode.SetPixelFormat(ref actual);

            IWICPalette? palette = null;
            if (actual == PixelFormat.Format8bppIndexed)
            {
                palette = wic.CreatePalette();
                owned.Add(palette);
                using var forPalette = wic.CreateFormatConverter();
                forPalette.Initialize(source, PixelFormat.Format32bppBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
                palette.InitializeFromBitmap(forPalette, 256, alpha);
                frameEncode.SetPalette(palette);
            }
            if (icc is not null)
            {
                using var cc = wic.CreateColorContext();
                cc.InitializeFromMemory(icc, (uint)icc.Length).CheckError();
                frameEncode.SetColorContexts([cc]);
            }

            using var converter = wic.CreateFormatConverter();
            converter.Initialize(source, actual, palette is null ? BitmapDitherType.None : BitmapDitherType.ErrorDiffusion,
                palette, alpha ? 50.0 : 0.0, palette is null ? BitmapPaletteType.Custom : BitmapPaletteType.Custom);

            if (CanHoldMetadata(target)) CopyMetadata(frame, frameEncode);

            // 帯ごとに書き込み、進捗の報告と中断を可能にする
            var bytesPerRow = Math.Max(1, prep.OutputWidth * 8);
            var rows = Math.Clamp(StripBytes / bytesPerRow, 1, prep.OutputHeight);
            for (var y = 0; y < prep.OutputHeight; y += rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = Math.Min(rows, prep.OutputHeight - y);
                frameEncode.WriteSource(converter, new Vortice.Mathematics.RectI(0, y, prep.OutputWidth, n)).CheckError();
                progress?.Report((y + n) / (double)prep.OutputHeight * 0.95);
            }
            frameEncode.Commit();
            encoder.Commit();
            progress?.Report(1.0);
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }

    private static Guid Container(ConversionFormat f) => f switch
    {
        ConversionFormat.Png => ContainerFormatGuids.Png,
        ConversionFormat.Jpeg => ContainerFormatGuids.Jpeg,
        ConversionFormat.Bmp => ContainerFormatGuids.Bmp,
        ConversionFormat.Gif => ContainerFormatGuids.Gif,
        ConversionFormat.Tiff => ContainerFormatGuids.Tiff,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>透過を白で合成する（JPEG・BMP など透過のない形式向け）。</summary>
    private static IWICBitmapSource CompositeOnWhite(IWICImagingFactory wic, IWICBitmapSource source, List<IDisposable> owned, CancellationToken cancellationToken)
    {
        using var premultiplied = wic.CreateFormatConverter();
        premultiplied.Initialize(source, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var (w, h) = (premultiplied.Size.Width, premultiplied.Size.Height);
        var stride = w * 4;
        var pixels = new byte[(long)stride * h];
        var rows = Math.Clamp(StripBytes / stride, 1, h);
        for (var y = 0; y < h; y += rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(rows, h - y);
            var span = pixels.AsSpan(y * stride, n * stride);
            premultiplied.CopyPixels(new Vortice.Mathematics.RectI(0, y, w, n), (uint)stride, span);
            for (var i = 0; i < span.Length; i += 4)
            {
                var inverse = 255 - span[i + 3];
                span[i] = (byte)Math.Min(255, span[i] + inverse);
                span[i + 1] = (byte)Math.Min(255, span[i + 1] + inverse);
                span[i + 2] = (byte)Math.Min(255, span[i + 2] + inverse);
                span[i + 3] = 255;
            }
        }
        var bitmap = wic.CreateBitmapFromMemory((uint)w, (uint)h, PixelFormat.Format32bppBGRA, pixels, (uint)stride);
        owned.Add(bitmap);
        return bitmap;
    }

    /// <summary>撮影日時・カメラ情報などを、読める・書ける範囲で引き継ぐ（仕様 §9.2「可能な範囲で保持」）。向き情報は画素に反映済みのため書かない。</summary>
    private static void CopyMetadata(IWICBitmapFrameDecode source, IWICBitmapFrameEncode destination)
    {
        try
        {
            using var reader = source.MetadataQueryReader;
            using var writer = destination.MetadataQueryWriter;
            if (reader is null || writer is null) return;
            foreach (var name in MetadataPolicies)
            {
                try
                {
                    var value = reader.GetMetadataByName(name);
                    if (value.Value is not null) writer.SetMetadataByName(name, value);
                }
                catch (SharpGenException)
                {
                    // 形式によって書けない項目は飛ばす
                }
            }
        }
        catch (SharpGenException e)
        {
            AppLog.Warn("メタデータを引き継げませんでした", e);
        }
    }

    // ---- WebP（FFmpeg の libwebp） ----

    private static async Task EncodeWebPAsync(ImageConversionPreparation prep, string path, FfmpegTools tools, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (pixels, icc) = await Task.Run(() =>
        {
            using var wic = new IWICImagingFactory();
            using var decoder = wic.CreateDecoderFromFileName(prep.InputPath, FileAccess.Read, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            var info = Inspect(wic, decoder, frame, prep.InputPath);
            var (source, outIcc, _, owned) = BuildSource(wic, frame, info, prep.Target);
            try
            {
                using var converter = wic.CreateFormatConverter();
                converter.Initialize(source, prep.HasTransparency ? PixelFormat.Format32bppBGRA : PixelFormat.Format24bppBGR,
                    BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
                var stride = prep.OutputWidth * (prep.HasTransparency ? 4 : 3);
                var buffer = new byte[(long)stride * prep.OutputHeight];
                converter.CopyPixels((uint)stride, buffer);
                return (buffer, outIcc);
            }
            finally
            {
                for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }, cancellationToken);
        progress?.Report(0.3);

        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-n",
            "-f", "rawvideo", "-pix_fmt", prep.HasTransparency ? "bgra" : "bgr24", "-s", $"{prep.OutputWidth}x{prep.OutputHeight}", "-i", "pipe:0",
            "-frames:v", "1", "-c:v", "libwebp",
        };
        args.AddRange(IsLosslessSource(prep.InputPath)
            ? ["-lossless", "1", "-compression_level", "6"]
            : ["-quality", WebPLossyQuality.ToString(System.Globalization.CultureInfo.InvariantCulture), "-compression_level", "6"]);
        args.AddRange(["-f", "webp", path]);

        await FfmpegRunner.RunAsync(tools, args, TimeSpan.Zero, null, cancellationToken,
            async (stdin, ct) => await stdin.WriteAsync(pixels, ct));
        progress?.Report(0.9);

        if (icc is not null)
        {
            var webp = await File.ReadAllBytesAsync(path, cancellationToken);
            await File.WriteAllBytesAsync(path, WebPContainer.AddIccProfile(webp, icc, prep.OutputWidth, prep.OutputHeight, prep.HasTransparency), cancellationToken);
        }
        progress?.Report(1.0);
    }

    // ---- 確認 ----

    /// <summary>書き出したファイルを読み戻し、寸法が正しいことを確認する（壊れた出力を完成扱いにしない）。</summary>
    private static void Verify(string path, ImageConversionPreparation prep)
    {
        try
        {
            using var wic = new IWICImagingFactory();
            using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            if (frame.Size.Width != prep.OutputWidth || frame.Size.Height != prep.OutputHeight)
                throw new InvalidDataException($"変換後の画像の大きさが正しくありません（{frame.Size.Width}x{frame.Size.Height}）。");
        }
        catch (SharpGenException e) when (prep.Target == ConversionFormat.WebP && e.HResult == unchecked((int)0x88982F50))
        {
            // WebP のデコーダー（Windows の拡張機能）がない環境では読み戻しによる確認を省く
            AppLog.Warn("WebP のデコーダーがないため、変換結果の読み戻し確認を省略しました");
        }
        catch (SharpGenException e)
        {
            throw new InvalidDataException("変換後の画像を読み戻せませんでした。", e);
        }
    }
}

/// <summary>WIC のコンテナ形式の GUID。</summary>
internal static class ContainerFormatGuids
{
    public static readonly Guid Png = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    public static readonly Guid Jpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    public static readonly Guid Bmp = new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3");
    public static readonly Guid Gif = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");
    public static readonly Guid Tiff = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");
}
