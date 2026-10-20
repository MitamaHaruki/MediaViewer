using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace MediaViewer.Tests.Color;

/// <summary>Direct2D の画像をオフスクリーンの FP16 へ等倍で描画し、画素値（乗算済みRGBA）を読み戻す。</summary>
internal static class D2DTestRenderer
{
    public static float[][] Draw(ID2D1DeviceContext5 ctx, ID2D1Image image, int width, int height)
    {
        var fmt = new PixelFormat(Format.R16G16B16A16_Float, AlphaMode.Premultiplied);
        var size = new SizeI(width, height);
        using var target = ctx.CreateBitmap(size, IntPtr.Zero, 0, new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target));
        using var readback = ctx.CreateBitmap(size, IntPtr.Zero, 0, new BitmapProperties1(fmt, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

        ctx.RenderingControls = new RenderingControls { BufferPrecision = BufferPrecision.PerChannel32Float };
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
