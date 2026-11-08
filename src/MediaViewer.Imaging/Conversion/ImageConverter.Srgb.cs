using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Decoding;
using Vortice.WIC;
using PixelFormat = Vortice.WIC.PixelFormat;

namespace MediaViewer.Imaging.Conversion;

/// <summary>色管理した sRGB の画素（32bpp、BGRA の順、アルファ乗算済み）。</summary>
public sealed record SrgbBitmap(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

public static partial class ImageConverter
{
    /// <summary>
    /// 印刷やサムネイル用に、画像を sRGB の画素へ変換する（仕様決定事項 D15・D17）。
    /// 埋め込み ICC・EXIF の色空間指定・CMYK・PNG の色情報を sRGB へ色変換し、EXIF の向きを反映する。
    /// 長辺が maxDimension を超える場合は縮小する（縮小を先に行い、巨大な画像でも速く小さく作る）。
    /// HDR 画像は SDR の sRGB に正しく変換できないため対応しない。
    /// </summary>
    public static SrgbBitmap RenderSrgb(string path, int maxDimension, CancellationToken cancellationToken)
    {
        using var wic = new IWICImagingFactory();
        using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        return RenderSrgb(wic, decoder, frame, Inspect(wic, decoder, frame, path), maxDimension, cancellationToken);
    }

    /// <summary>メモリ上の画像データ（音声ファイルのアルバムアートなど）から sRGB の画素を作る。</summary>
    public static SrgbBitmap RenderSrgb(byte[] data, int maxDimension, CancellationToken cancellationToken)
    {
        using var wic = new IWICImagingFactory();
        using var stream = new MemoryStream(data, writable: false);
        using var decoder = wic.CreateDecoderFromStream(stream, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var info = Inspect(wic, decoder, frame, () => new MemoryStream(data, writable: false));
        return RenderSrgb(wic, decoder, frame, info, maxDimension, cancellationToken);
    }

    private static SrgbBitmap RenderSrgb(IWICImagingFactory wic, IWICBitmapDecoder decoder, IWICBitmapFrameDecode frame, SourceInfo info,
        int maxDimension, CancellationToken cancellationToken)
    {
        if (info.Color.IsHdr) throw new NotSupportedException("HDR 画像は印刷・サムネイルに対応していません。");
        cancellationToken.ThrowIfCancellationRequested();

        var owned = new List<IDisposable>();
        try
        {
            IWICBitmapSource source = frame;
            var scale = Math.Min(1.0, maxDimension / (double)Math.Max(info.Width, info.Height));
            var width = Math.Max(1, (int)Math.Round(info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(info.Height * scale));

            // CMYK は色変換してから縮小する（CMYK のまま縮小できない場合があるため）
            if (scale < 1 && !info.IsCmyk) source = Scale(wic, source, width, height, owned);

            var from = info.IsCmyk
                ? info.CmykProfile ?? WicImageLoader.DefaultCmykProfile() ?? throw new NotSupportedException("CMYKプロファイルが見つかりません。")
                : OutputIcc(info);
            if (from is not null) source = Transform(wic, source, from, IccProfileFactory.Srgb, false, owned);

            if (scale < 1 && info.IsCmyk) source = Scale(wic, source, width, height, owned);

            if (info.Orientation != ExifOrientation.Normal)
            {
                var rotator = wic.CreateBitmapFlipRotator();
                owned.Add(rotator);
                rotator.Initialize(source, ToWicTransform(info.Orientation));
                source = rotator;
            }

            var converter = wic.CreateFormatConverter();
            owned.Add(converter);
            converter.Initialize(source, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            var size = converter.Size;
            var pixels = new byte[size.Width * size.Height * 4];
            cancellationToken.ThrowIfCancellationRequested();
            converter.CopyPixels((uint)(size.Width * 4), pixels);
            return new SrgbBitmap(size.Width, size.Height, pixels);
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }

    private static IWICBitmapSource Scale(IWICImagingFactory wic, IWICBitmapSource source, int width, int height, List<IDisposable> owned)
    {
        var scaler = wic.CreateBitmapScaler();
        owned.Add(scaler);
        scaler.Initialize(source, (uint)width, (uint)height, BitmapInterpolationMode.HighQualityCubic);
        return scaler;
    }
}
