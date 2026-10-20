using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Graphics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.WIC;
using PixelFormat = Vortice.WIC.PixelFormat;
using ColorContextType = Vortice.WIC.ColorContextType;

namespace MediaViewer.Imaging.Decoding;

/// <summary>
/// WIC で画像をデコードし、Direct2D の画像として GPU へ転送する。
/// 呼び出しスレッドで同期的に処理するため、UIスレッド以外から呼ぶこと。
/// </summary>
public static class WicImageLoader
{
    private const ushort ExifColorSpaceSrgb = 1;
    private const ushort ExifColorSpaceAdobeRgb = 2;
    internal static readonly Guid ContainerFormatPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    internal static readonly Guid ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    internal static readonly Guid[] CmykFormats =
    [
        PixelFormat.Format32bppCMYK,
        PixelFormat.Format64bppCMYK,
        PixelFormat.Format40bppCMYKAlpha,
        PixelFormat.Format80bppCMYKAlpha,
    ];

    public static LoadedImage Load(GraphicsDevice device, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var decoder = device.WicFactory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        return LoadCore(device, decoder, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096), maxDimension: 0, cancellationToken);
    }

    /// <summary>
    /// メモリ上の画像（アルバムアート等）を読み込む。maxDimension を超える場合は縮小してからGPUへ転送し、
    /// 不要なメモリ増加を避ける（仕様 §7.4）。
    /// </summary>
    public static LoadedImage LoadFromMemory(GraphicsDevice device, byte[] data, int maxDimension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = device.WicFactory.CreateStream(data);
        using var decoder = device.WicFactory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        return LoadCore(device, decoder, () => new MemoryStream(data, writable: false), maxDimension, cancellationToken);
    }

    private static LoadedImage LoadCore(GraphicsDevice device, IWICBitmapDecoder decoder, Func<Stream> openRaw, int maxDimension, CancellationToken cancellationToken)
    {
        var wic = device.WicFactory;
        var frameCount = (int)decoder.FrameCount;
        var container = decoder.ContainerFormat;
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        var sourceFormat = frame.PixelFormat;
        var isCmyk = CmykFormats.Contains(sourceFormat);

        var isHighBitDepth = IsHighBitDepth(wic, sourceFormat);
        var (color, cmykProfile) = ReadSourceColor(wic, frame, container, openRaw, isCmyk);
        var orientation = ReadOrientation(frame);

        cancellationToken.ThrowIfCancellationRequested();

        IWICBitmapSource source = frame;
        using var cmykTransform = isCmyk ? CreateCmykTransform(wic, frame, cmykProfile, out color) : null;
        if (cmykTransform is not null)
        {
            source = cmykTransform;
            isHighBitDepth = true;
        }
        // 高ビット深度は 16bit 整数のまま扱う。WIC は整数→浮動小数の変換で sRGB ガンマを外してしまう
        // （浮動小数を線形とみなす）ため、ICC やPQの符号化値を保つには浮動小数へ変換してはいけない
        var targetFormat = isHighBitDepth ? PixelFormat.Format64bppPRGBA : PixelFormat.Format32bppPBGRA;

        IWICBitmapScaler? scaler = null;
        if (maxDimension > 0 && Math.Max(size.Width, size.Height) > maxDimension)
        {
            var ratio = (double)maxDimension / Math.Max(size.Width, size.Height);
            var w = Math.Max(1, (int)Math.Round(size.Width * ratio));
            var h = Math.Max(1, (int)Math.Round(size.Height * ratio));
            scaler = wic.CreateBitmapScaler();
            scaler.Initialize(source, (uint)w, (uint)h, Vortice.WIC.BitmapInterpolationMode.HighQualityCubic);
            source = scaler;
            size = new Vortice.Mathematics.SizeI(w, h);
        }

        using var _ = scaler;
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(source, targetFormat, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

        using var ctx = device.CreateDeviceContext();
        ID2D1Image image;
        if (size.Width <= ctx.MaximumBitmapSize && size.Height <= ctx.MaximumBitmapSize)
        {
            image = UploadInStrips(ctx, converter, size.Width, size.Height, isHighBitDepth, cancellationToken);
        }
        else
        {
            // GPUの最大テクスチャサイズを超える巨大画像は、タイル分割に対応した ImageSource で扱う
            using var decoded = wic.CreateBitmapFromSource(converter, BitmapCreateCacheOption.CacheOnLoad);
            cancellationToken.ThrowIfCancellationRequested();
            image = ctx.CreateImageSourceFromWic(decoded, ImageSourceLoadingOptions.CacheOnDemand, AlphaMode.Premultiplied);
        }

        return new LoadedImage
        {
            Image = image,
            PixelWidth = size.Width,
            PixelHeight = size.Height,
            Orientation = orientation,
            Color = color,
            IsHighBitDepth = isHighBitDepth,
            FrameCount = frameCount,
        };
    }

    /// <summary>1回にデコードして転送する帯の大きさ（バイト）。</summary>
    private const int StripBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 画像を上から帯状にデコードし、そのまま GPU のビットマップへ書き込む。
    /// CPU 側にフル解像度のコピーを作らない（仕様 §5.1）。帯ごとに中断を受け付ける。
    /// </summary>
    private static ID2D1Bitmap1 UploadInStrips(ID2D1DeviceContext5 ctx, IWICBitmapSource source, int width, int height, bool highBitDepth, CancellationToken cancellationToken)
    {
        var bytesPerPixel = highBitDepth ? 8 : 4;
        var stride = width * bytesPerPixel;
        var format = highBitDepth ? Vortice.DXGI.Format.R16G16B16A16_UNorm : Vortice.DXGI.Format.B8G8R8A8_UNorm;
        var bitmap = ctx.CreateBitmap(new Vortice.Mathematics.SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(format, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        try
        {
            var rowsPerStrip = Math.Clamp(StripBytes / stride, 1, height);
            var buffer = new byte[stride * rowsPerStrip];
            for (var y = 0; y < height; y += rowsPerStrip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = Math.Min(rowsPerStrip, height - y);
                var strip = buffer.AsSpan(0, stride * rows);
                source.CopyPixels(new Vortice.Mathematics.RectI(0, y, width, rows), (uint)stride, strip);
                bitmap.CopyFromMemory(new System.Drawing.Rectangle(0, y, width, rows), (ReadOnlySpan<byte>)strip, (uint)stride).CheckError();
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static bool IsHighBitDepth(IWICImagingFactory wic, Guid format)
    {
        try
        {
            using var info = wic.CreateComponentInfo(format);
            using var pf = info.QueryInterface<IWICPixelFormatInfo>();
            var channels = pf.ChannelCount;
            return channels > 0 && pf.BitsPerPixel / channels > 8;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 画像の色空間を決める。優先順位：
    /// PNG cICP → 埋め込みICC（RGB／グレー／CMYK） → PNG sRGB・gAMA/cHRM → EXIF の色空間指定 → sRGB（仕様 §5.2-2）。
    /// CMYK の場合は CMYK プロファイル（あれば）を返し、変換は呼び出し側で行う。
    /// </summary>
    internal static (SourceColor Color, byte[]? CmykProfile) ReadSourceColor(
        IWICImagingFactory wic, IWICBitmapFrameDecode frame, Guid container, Func<Stream> openRaw, bool isCmyk)
    {
        PngColorChunks? png = null;
        ExifColorInfo? exif = null;
        try
        {
            if (container == ContainerFormatPng) { using var s = openRaw(); png = ColorMetadataReader.ReadPng(s); }
            else if (container == ContainerFormatJpeg) { using var s = openRaw(); exif = ColorMetadataReader.ReadJpegExif(s); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // メタデータが読めなくても画像自体は表示する
        }

        string? note = null;
        if (png?.Cicp is { } cicp)
        {
            var fromCicp = FromCicp(cicp, png.MaxContentLightLevel, out note);
            if (fromCicp is not null) return (fromCicp, null);
        }

        IWICColorContext[] contexts;
        try
        {
            contexts = frame.TryGetColorContexts(wic) ?? [];
        }
        catch
        {
            contexts = [];
        }

        try
        {
            string? rejected = null;
            byte[]? cmykProfile = null;
            foreach (var c in contexts)
            {
                if (c.Type != ColorContextType.Profile) continue;
                var bytes = ReadProfileBytes(c);
                if (bytes is not { Length: > 0 })
                {
                    rejected ??= "ICCプロファイルを読み取れません";
                    continue;
                }
                switch (IccProfileFactory.ColorSpaceOf(bytes))
                {
                    case "RGB " when SourceColor.IsSupportedRgbIcc(bytes, out var reason):
                        if (!isCmyk) return (new SourceColor(SourceColorKind.EmbeddedIcc, bytes), null);
                        rejected ??= reason;
                        break;
                    case "GRAY" when !isCmyk:
                        // グレーの階調特性（kTRC）を RGB プロファイルに写して、同じ描画経路で扱う
                        if (IccProfileFactory.CreateRgbFromGray(bytes) is { } rgb)
                            return (new SourceColor(SourceColorKind.GrayIcc, rgb), null);
                        rejected ??= "グレーICCのトーンカーブを解釈できません";
                        break;
                    case "CMYK" when isCmyk:
                        cmykProfile ??= bytes;
                        break;
                    default:
                        rejected ??= SourceColor.IsSupportedRgbIcc(bytes, out var r) ? "画素の形式とICCの色空間が一致しません" : r;
                        break;
                }
            }
            if (isCmyk) return (SourceColor.Srgb, cmykProfile);
            if (rejected is not null) return (new SourceColor(SourceColorKind.InvalidIccFallbackSrgb, null, rejected), null);

            if (png is { HasIccp: false } && FromPngGamma(png) is { } fromGamma) return (fromGamma, null);

            foreach (var c in contexts)
            {
                if (c.Type != ColorContextType.ExifColorSpace) continue;
                switch (c.ExifColorSpace)
                {
                    case ExifColorSpaceSrgb:
                        return (new SourceColor(SourceColorKind.ExifSrgb, null), null);
                    case ExifColorSpaceAdobeRgb:
                        return (new SourceColor(SourceColorKind.ExifAdobeRgb, null), null);
                }
            }
            // ICCなしで「未校正」＋互換性インデックス R03 はカメラの Adobe RGB モード（DCF 規格）
            if (exif is { ColorSpace: 0xFFFF, InteropIndex: "R03" })
            {
                return (new SourceColor(SourceColorKind.ExifAdobeRgb, null, "EXIF 互換性インデックス R03"), null);
            }
            return (note is null ? SourceColor.Srgb : new SourceColor(SourceColorKind.AssumedSrgb, null, note), null);
        }
        finally
        {
            foreach (var c in contexts) c.Dispose();
        }
    }

    /// <summary>PNG の cICP（ITU-T H.273）を色空間にする。対応外は null と理由を返す。</summary>
    private static SourceColor? FromCicp(Cicp cicp, double? maxCll, out string? unsupported)
    {
        unsupported = null;
        RgbPrimaries? primaries = cicp.ColourPrimaries switch
        {
            1 => RgbPrimaries.Srgb,
            9 => RgbPrimaries.Bt2020,
            12 => RgbPrimaries.DisplayP3,
            _ => null,
        };
        if (cicp.MatrixCoefficients != 0 || cicp.VideoFullRangeFlag != 1 || primaries is null)
        {
            unsupported = $"PNG の cICP（原色 {cicp.ColourPrimaries}、行列 {cicp.MatrixCoefficients}）は未対応のため、sRGB として表示しています";
            return null;
        }
        if (cicp.TransferCharacteristics == 16)
        {
            if (cicp.ColourPrimaries == 9)
                return new SourceColor(SourceColorKind.HdrPq, null, HdrMaxNits: maxCll is > 0 ? (float)maxCll.Value : 0);
            unsupported = "BT.2020 以外の原色の PQ は未対応のため、sRGB として表示しています";
            return null;
        }
        IccProfileFactory.TransferFunction? transfer = cicp.TransferCharacteristics switch
        {
            13 => IccProfileFactory.SrgbToLinear,
            8 => v => v,
            4 => IccProfileFactory.Gamma(2.2),
            // BT.709 等のカメラ側カーブは、表示側の BT.1886（ガンマ2.4）で解釈する
            1 or 6 or 14 or 15 => IccProfileFactory.Gamma(2.4),
            _ => null,
        };
        if (transfer is null)
        {
            unsupported = $"PNG の cICP の伝達特性 {cicp.TransferCharacteristics}（HLG 等）は未対応のため、sRGB として表示しています";
            return null;
        }
        var icc = IccProfileFactory.CreateRgb(primaries, transfer, $"PNG cICP {cicp.ColourPrimaries}/{cicp.TransferCharacteristics}");
        return new SourceColor(SourceColorKind.PngCicp, icc);
    }

    /// <summary>PNG の sRGB / gAMA / cHRM（iCCP がない場合のみ使う）。</summary>
    private static SourceColor? FromPngGamma(PngColorChunks png)
    {
        if (png.HasSrgb) return new SourceColor(SourceColorKind.PngSrgb, null);
        if (png.FileGamma is null && png.Chromaticities is null) return null;
        var gamma = png.FileGamma is > 0 ? 1 / png.FileGamma.Value : (double?)null;
        // gAMA 1/2.2 だけの指定は、多くの画像で sRGB の意図なので sRGB として扱う
        if (png.Chromaticities is null && gamma is { } g && Math.Abs(g - 2.2) < 0.01)
        {
            return new SourceColor(SourceColorKind.PngGamma, null, "gAMA 1/2.2 は sRGB として扱う");
        }
        var transfer = gamma is { } gm ? IccProfileFactory.Gamma(gm) : IccProfileFactory.SrgbToLinear;
        var icc = IccProfileFactory.CreateRgb(png.Chromaticities ?? RgbPrimaries.Srgb, transfer, "PNG gAMA/cHRM");
        return new SourceColor(SourceColorKind.PngGamma, icc);
    }

    /// <summary>
    /// CMYK 画像を ICC で作業用の広色域 RGB（BT.2020・16bit）へ変換する。
    /// 埋め込みプロファイルがなければ Windows 標準の CMYK プロファイル（SWOP）を使い、その旨を通知する。
    /// </summary>
    private static IWICColorTransform CreateCmykTransform(IWICImagingFactory wic, IWICBitmapSource frame, byte[]? embedded, out SourceColor color)
    {
        var profile = embedded ?? DefaultCmykProfile()
            ?? throw new NotSupportedException("CMYKプロファイルが見つからないため、CMYK画像を表示できません。");
        using var src = wic.CreateColorContext();
        src.InitializeFromMemory(profile, (uint)profile.Length).CheckError();
        var working = IccProfileFactory.WideGamutWorkingSpace;
        using var dst = wic.CreateColorContext();
        dst.InitializeFromMemory(working, (uint)working.Length).CheckError();
        var transform = wic.CreateColorTransformer();
        Exception? last = null;
        foreach (var format in CmykOutputFormats)
        {
            try
            {
                transform.Initialize(frame, src, dst, format);
                last = null;
                break;
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                last = e;
            }
        }
        if (last is not null)
        {
            transform.Dispose();
            throw new NotSupportedException("CMYK画像の色変換に対応していない形式です。", last);
        }
        color = new SourceColor(SourceColorKind.CmykConverted, working,
            embedded is null ? "CMYKプロファイルがないため、Windows 標準の CMYK（SWOP）として変換しました" : null);
        return transform;
    }

    private static readonly Guid[] CmykOutputFormats =
    [
        PixelFormat.Format64bppRGBA,
        PixelFormat.Format48bppRGB,
        PixelFormat.Format64bppBGRA,
        PixelFormat.Format48bppBGR,
        PixelFormat.Format32bppBGR,
        PixelFormat.Format24bppBGR,
    ];

    internal static byte[]? DefaultCmykProfile()
    {
        var path = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color", "RSWOP.icm");
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static unsafe byte[]? ReadProfileBytes(IWICColorContext context)
    {
        if (context.GetProfileBytes(0, null, out var size).Failure || size == 0) return null;
        var bytes = new byte[size];
        return context.GetProfileBytes(bytes, out _).Success ? bytes : null;
    }

    internal static ExifOrientation ReadOrientation(IWICBitmapFrameDecode frame)
    {
        try
        {
            using var reader = frame.MetadataQueryReader;
            if (reader is null) return ExifOrientation.Normal;
            var value = reader.GetMetadataByName("System.Photo.Orientation").Value;
            var o = value switch
            {
                ushort u => u,
                short s => (ushort)s,
                uint ui => (ushort)ui,
                int i => (ushort)i,
                _ => (ushort)1,
            };
            return o is >= 1 and <= 8 ? (ExifOrientation)o : ExifOrientation.Normal;
        }
        catch
        {
            // メタデータを持たない形式・壊れたメタデータは回転なしとして扱う
            return ExifOrientation.Normal;
        }
    }
}
