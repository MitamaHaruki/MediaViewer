using MediaViewer.Imaging.Color;
using Vortice.Direct2D1;

namespace MediaViewer.Imaging.Decoding;

/// <summary>
/// GPUへ転送済みの画像。デコード済みのCPU側コピーは転送後に解放する（仕様 §5.1：不要なフル解像度コピーを増やさない）。
/// </summary>
public sealed class LoadedImage : IDisposable
{
    public required ID2D1Image Image { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required ExifOrientation Orientation { get; init; }
    public required SourceColor Color { get; init; }
    public required bool IsHighBitDepth { get; init; }
    public required int FrameCount { get; init; }

    /// <summary>正しい向きに直した後の幅（ピクセル）。</summary>
    public int UprightWidth => Orientation.SwapsAxes() ? PixelHeight : PixelWidth;

    /// <summary>正しい向きに直した後の高さ（ピクセル）。</summary>
    public int UprightHeight => Orientation.SwapsAxes() ? PixelWidth : PixelHeight;

    public void Dispose() => Image.Dispose();
}
