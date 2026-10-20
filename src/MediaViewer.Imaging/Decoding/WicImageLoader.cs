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

    private static readonly Guid[] CmykFormats =
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
        return LoadCore(device, decoder, maxDimension: 0, cancellationToken);
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
        return LoadCore(device, decoder, maxDimension, cancellationToken);
    }

    private static LoadedImage LoadCore(GraphicsDevice device, IWICBitmapDecoder decoder, int maxDimension, CancellationToken cancellationToken)
    {
        var wic = device.WicFactory;
        var frameCount = (int)decoder.FrameCount;
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        var sourceFormat = frame.PixelFormat;

        if (CmykFormats.Contains(sourceFormat))
        {
            throw new NotSupportedException("CMYK画像の表示は現在未対応です。");
        }

        var isHighBitDepth = IsHighBitDepth(wic, sourceFormat);
        var targetFormat = isHighBitDepth ? PixelFormat.Format64bppPRGBAHalf : PixelFormat.Format32bppPBGRA;
        var color = ReadSourceColor(wic, frame);
        var orientation = ReadOrientation(frame);

        cancellationToken.ThrowIfCancellationRequested();

        IWICBitmapSource source = frame;
        IWICBitmapScaler? scaler = null;
        if (maxDimension > 0 && Math.Max(size.Width, size.Height) > maxDimension)
        {
            var ratio = (double)maxDimension / Math.Max(size.Width, size.Height);
            var w = Math.Max(1, (int)Math.Round(size.Width * ratio));
            var h = Math.Max(1, (int)Math.Round(size.Height * ratio));
            scaler = wic.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)w, (uint)h, Vortice.WIC.BitmapInterpolationMode.HighQualityCubic);
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
            // デコード（CPU）→ GPU転送。CPU側のデコード結果はこのメソッドを抜けると解放される。
            using var decoded = wic.CreateBitmapFromSource(converter, BitmapCreateCacheOption.CacheOnLoad);
            cancellationToken.ThrowIfCancellationRequested();
            image = ctx.CreateBitmapFromWicBitmap(decoded, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(isHighBitDepth ? Vortice.DXGI.Format.R16G16B16A16_Float : Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                96, 96, BitmapOptions.None));
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

    private static bool IsHighBitDepth(IWICImagingFactory wic, Guid format)
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

    private static SourceColor ReadSourceColor(IWICImagingFactory wic, IWICBitmapFrameDecode frame)
    {
        IWICColorContext[] contexts;
        try
        {
            contexts = frame.TryGetColorContexts(wic) ?? [];
        }
        catch
        {
            return SourceColor.Srgb;
        }

        try
        {
            string? rejected = null;
            // ICCプロファイルを優先し、なければEXIFの色空間指定を使う
            foreach (var c in contexts)
            {
                if (c.Type != ColorContextType.Profile) continue;
                var bytes = ReadProfileBytes(c);
                string? reason = "ICCプロファイルを読み取れません";
                if (bytes is { Length: > 0 } && SourceColor.IsSupportedRgbIcc(bytes, out reason))
                {
                    return new SourceColor(SourceColorKind.EmbeddedIcc, bytes);
                }
                rejected ??= reason;
            }
            foreach (var c in contexts)
            {
                if (c.Type != ColorContextType.ExifColorSpace) continue;
                switch (c.ExifColorSpace)
                {
                    case ExifColorSpaceSrgb:
                        return new SourceColor(SourceColorKind.ExifSrgb, null);
                    case ExifColorSpaceAdobeRgb:
                        return new SourceColor(SourceColorKind.ExifAdobeRgb, null);
                }
            }
            return rejected is null
                ? SourceColor.Srgb
                : new SourceColor(SourceColorKind.InvalidIccFallbackSrgb, null, rejected);
        }
        finally
        {
            foreach (var c in contexts) c.Dispose();
        }
    }

    private static unsafe byte[]? ReadProfileBytes(IWICColorContext context)
    {
        if (context.GetProfileBytes(0, null, out var size).Failure || size == 0) return null;
        var bytes = new byte[size];
        return context.GetProfileBytes(bytes, out _).Success ? bytes : null;
    }

    private static ExifOrientation ReadOrientation(IWICBitmapFrameDecode frame)
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
