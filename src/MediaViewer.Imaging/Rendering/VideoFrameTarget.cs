using System.Runtime.InteropServices;
using MediaViewer.Imaging.Graphics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MediaViewer.Imaging.Rendering;

/// <summary>
/// 動画フレームの受け皿となる GPU テクスチャ。Windows の MediaPlayer（フレームサーバーモード）が
/// <see cref="Surface"/> へ直接フレームをコピーし、<see cref="Image"/> として画像と同じ経路で描画する。
/// CPU へのフレーム読み戻しは発生しない（仕様 §7.5）。
/// </summary>
public sealed partial class VideoFrameTarget : IDisposable
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID2D1Bitmap1 _bitmap;

    public int Width { get; }
    public int Height { get; }

    /// <summary>MediaPlayer.CopyFrameToVideoSurface に渡す WinRT サーフェス。</summary>
    public IDirect3DSurface Surface { get; }

    /// <summary>描画用の Direct2D 画像（sRGB 符号化の BGRA8）。</summary>
    public ID2D1Image Image => _bitmap;

    private VideoFrameTarget(ID3D11Texture2D texture, ID2D1Bitmap1 bitmap, IDirect3DSurface surface, int width, int height)
    {
        _texture = texture;
        _bitmap = bitmap;
        Surface = surface;
        Width = width;
        Height = height;
    }

    public static VideoFrameTarget Create(GraphicsDevice device, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var texture = device.D3DDevice.CreateTexture2D(desc);
        try
        {
            using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
            using var ctx = device.CreateDeviceContext();
            var bitmap = ctx.CreateBitmapFromDxgiSurface(dxgiSurface, new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.None));
            var surface = CreateWinRtSurface(dxgiSurface.NativePointer);
            return new VideoFrameTarget(texture, bitmap, surface, width, height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static IDirect3DSurface CreateWinRtSurface(IntPtr dxgiSurface)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out var inspectable));
        try
        {
            return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    public void Dispose()
    {
        (Surface as IDisposable)?.Dispose();
        _bitmap.Dispose();
        _texture.Dispose();
    }
}
