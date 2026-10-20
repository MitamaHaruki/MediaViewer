namespace MediaViewer.Tests.Color;

/// <summary>
/// テストの期待値を Direct2D と独立に計算するための色計算（原色・白色点から行列を求める教科書的な方法）。
/// </summary>
internal static class ColorMath
{
    public readonly record struct Xy(double X, double Y);

    public sealed record RgbSpace(string Name, Xy Red, Xy Green, Xy Blue, Xy White);

    public static readonly Xy D65 = new(0.3127, 0.3290);
    public static readonly double[] D50Xyz = [0.9642, 1.0, 0.8249];

    public static readonly RgbSpace Srgb = new("sRGB", new(0.64, 0.33), new(0.30, 0.60), new(0.15, 0.06), D65);
    public static readonly RgbSpace DisplayP3 = new("Display P3", new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), D65);
    public static readonly RgbSpace AdobeRgb = new("Adobe RGB (1998)", new(0.64, 0.33), new(0.21, 0.71), new(0.15, 0.06), D65);

    private static double[] ToXyz(Xy c) => [c.X / c.Y, 1.0, (1 - c.X - c.Y) / c.Y];

    /// <summary>線形RGB → XYZ（白色点はそのまま）の3x3行列。</summary>
    public static double[,] RgbToXyz(RgbSpace s)
    {
        var r = ToXyz(s.Red);
        var g = ToXyz(s.Green);
        var b = ToXyz(s.Blue);
        var m = new double[,] { { r[0], g[0], b[0] }, { r[1], g[1], b[1] }, { r[2], g[2], b[2] } };
        var w = ToXyz(s.White);
        var k = Multiply(Invert(m), w);
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                m[row, col] *= k[col];
        return m;
    }

    /// <summary>Bradford 法による白色点変換行列（from → to、いずれもXYZ）。</summary>
    public static double[,] Bradford(double[] fromXyz, double[] toXyz)
    {
        double[,] ma = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };
        var src = Multiply(ma, fromXyz);
        var dst = Multiply(ma, toXyz);
        var scale = new double[,] { { dst[0] / src[0], 0, 0 }, { 0, dst[1] / src[1], 0 }, { 0, 0, dst[2] / src[2] } };
        return Multiply(Multiply(Invert(ma), scale), ma);
    }

    public static double[] WhiteXyz(Xy w) => ToXyz(w);

    /// <summary>線形RGB（src）→ 線形RGB（dst）。白色点が同じ前提。</summary>
    public static double[] Convert(RgbSpace src, RgbSpace dst, double[] linearRgb) =>
        Multiply(Invert(RgbToXyz(dst)), Multiply(RgbToXyz(src), linearRgb));

    public static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double[] Multiply(double[,] m, double[] v) =>
    [
        m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
        m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
        m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2],
    ];

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return r;
    }

    public static double[,] Invert(double[,] m)
    {
        var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        var r = new double[3, 3];
        r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return r;
    }
}
