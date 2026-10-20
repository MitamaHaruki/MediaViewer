using System.Numerics;

namespace MediaViewer.Imaging.Decoding;

/// <summary>EXIF Orientation（1〜8）。</summary>
public enum ExifOrientation : ushort
{
    Normal = 1,
    FlipHorizontal = 2,
    Rotate180 = 3,
    FlipVertical = 4,
    Transpose = 5,
    Rotate90 = 6,
    Transverse = 7,
    Rotate270 = 8,
}

public static class ExifOrientationExtensions
{
    /// <summary>縦横が入れ替わる向きか。</summary>
    public static bool SwapsAxes(this ExifOrientation o) => o is >= ExifOrientation.Transpose and <= ExifOrientation.Rotate270;

    /// <summary>
    /// 元画像のピクセル座標 → 正しい向きに直した座標 への変換行列。
    /// 正しい向きの画像の左上が原点、幅 w・高さ h は元画像の寸法。
    /// </summary>
    public static Matrix3x2 ToUprightMatrix(this ExifOrientation o, float w, float h) => o switch
    {
        ExifOrientation.FlipHorizontal => new Matrix3x2(-1, 0, 0, 1, w, 0),
        ExifOrientation.Rotate180 => new Matrix3x2(-1, 0, 0, -1, w, h),
        ExifOrientation.FlipVertical => new Matrix3x2(1, 0, 0, -1, 0, h),
        ExifOrientation.Transpose => new Matrix3x2(0, 1, 1, 0, 0, 0),
        ExifOrientation.Rotate90 => new Matrix3x2(0, 1, -1, 0, h, 0),
        ExifOrientation.Transverse => new Matrix3x2(0, -1, -1, 0, h, w),
        ExifOrientation.Rotate270 => new Matrix3x2(0, -1, 1, 0, 0, w),
        _ => Matrix3x2.Identity,
    };
}
