using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.WIC;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace MediaViewer.Imaging.Graphics;

/// <summary>
/// Direct3D 11 / Direct2D / WIC の共有デバイス。アプリ全体で1つを使い回す。
/// Direct2D はマルチスレッドファクトリで作成し、読込みスレッドでのビットマップ生成と
/// UIスレッドでの描画を並行させられるようにする（各スレッドは自分専用のデバイスコンテキストを使う）。
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    public ID3D11Device D3DDevice { get; }
    public IDXGIDevice DxgiDevice { get; }
    public IDXGIFactory2 DxgiFactory { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public IWICImagingFactory WicFactory { get; }
    public bool IsSoftware { get; }

    private GraphicsDevice(ID3D11Device d3d, bool isSoftware)
    {
        D3DDevice = d3d;
        IsSoftware = isSoftware;
        // 動画フレームのコピー（Media Foundation）と Direct2D 描画が別スレッドから同じデバイスを使うため保護する
        using (var mt = d3d.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }
        DxgiDevice = d3d.QueryInterface<IDXGIDevice>();
        DxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(DxgiDevice);
        WicFactory = new IWICImagingFactory();
    }

    /// <summary>ハードウェアデバイスを作成する。失敗時（ドライバー不調・リモート環境等）はWARPにフォールバックする。</summary>
    public static GraphicsDevice Create(bool forceSoftware = false)
    {
        if (!forceSoftware && TryCreateD3D(DriverType.Hardware, out var hw))
        {
            return new GraphicsDevice(hw, isSoftware: false);
        }
        if (TryCreateD3D(DriverType.Warp, out var warp))
        {
            return new GraphicsDevice(warp, isSoftware: true);
        }
        throw new InvalidOperationException("Direct3D 11 デバイスを作成できませんでした。");
    }

    private static bool TryCreateD3D(DriverType driverType, out ID3D11Device device)
    {
        // VideoSupport：Media Foundation が動画フレームをこのデバイスのテクスチャへ書き込めるようにする
        var result = D3D11.D3D11CreateDevice(
            null,
            driverType,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            FeatureLevels,
            out device!);
        if (result.Success && device is not null) return true;
        result = D3D11.D3D11CreateDevice(null, driverType, DeviceCreationFlags.BgraSupport, FeatureLevels, out device!);
        return result.Success && device is not null;
    }

    /// <summary>呼び出し元スレッド専用の Direct2D デバイスコンテキストを作成する。</summary>
    public ID2D1DeviceContext5 CreateDeviceContext()
    {
        using var ctx = D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        return ctx.QueryInterface<ID2D1DeviceContext5>();
    }

    public void Dispose()
    {
        WicFactory.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        DxgiFactory.Dispose();
        DxgiDevice.Dispose();
        D3DDevice.Dispose();
    }
}
