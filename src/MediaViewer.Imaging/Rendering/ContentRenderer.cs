using System.Numerics;
using MediaViewer.Imaging.Color;
using MediaViewer.Imaging.Graphics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;

namespace MediaViewer.Imaging.Rendering;

/// <summary>
/// 画像・動画フレームを、色変換したうえで FP16 scRGB のスワップチェーンへ描画する。
/// 画像と動画は同じ描画経路・同じ表示変換（倍率・位置）を使う。
/// UIスレッドと動画フレームのスレッドから呼ばれるため、全操作をロックで直列化する。
/// </summary>
public sealed class ContentRenderer : IDisposable
{
    private const Format SwapChainFormat = Format.R16G16B16A16_Float;

    /// <summary>背景色（sRGB 0x10,0x10,0x10）を線形化した値。</summary>
    private const float BackgroundLinear = 0.0052f;

    private readonly object _gate = new();
    private readonly GraphicsDevice _device;
    private readonly ID2D1DeviceContext5 _ctx;

    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _target;
    private int _width;
    private int _height;

    private ID2D1Image? _source;
    private SourceColor _sourceColor = SourceColor.Srgb;
    private Matrix3x2 _upright = Matrix3x2.Identity;
    private ColorPipeline? _pipeline;
    private DisplayColorInfo _display = DisplayColorInfo.FallbackSrgb;

    private Matrix3x2 _view = Matrix3x2.Identity;
    private float _viewScale = 1f;
    private bool _disposed;

    public ContentRenderer(GraphicsDevice device)
    {
        _device = device;
        _ctx = device.CreateDeviceContext();
        // 中間バッファを浮動小数にし、色域外（負値・1超）の値を保持する
        _ctx.RenderingControls = new RenderingControls { BufferPrecision = BufferPrecision.PerChannel32Float, TileSize = default };
    }

    public GraphicsDevice Device => _device;

    /// <summary>現在の色変換経路の説明（診断表示用）。</summary>
    public string ColorDescription
    {
        get { lock (_gate) return _pipeline?.Description ?? "（表示なし）"; }
    }

    public DisplayColorInfo Display
    {
        get { lock (_gate) return _display; }
    }

    /// <summary>XAML の SwapChainPanel に渡すスワップチェーン。初回呼び出しで作成する。</summary>
    public IDXGISwapChain1 GetOrCreateSwapChain(int pixelWidth, int pixelHeight)
    {
        lock (_gate)
        {
            if (_swapChain is not null) return _swapChain;
            _width = Math.Max(1, pixelWidth);
            _height = Math.Max(1, pixelHeight);
            var desc = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = SwapChainFormat,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            _swapChain = _device.DxgiFactory.CreateSwapChainForComposition(_device.D3DDevice, desc, null);
            using (var sc3 = _swapChain.QueryInterface<IDXGISwapChain3>())
            {
                // scRGB（線形・sRGB原色・拡張範囲）として扱うようOSへ伝える
                sc3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
            }
            CreateTarget();
            return _swapChain;
        }
    }

    /// <summary>スワップチェーンのピクセル数と、DPIスケールの逆変換を設定する。</summary>
    public void Resize(int pixelWidth, int pixelHeight, float compositionScaleX, float compositionScaleY)
    {
        lock (_gate)
        {
            if (_swapChain is null) return;
            pixelWidth = Math.Max(1, pixelWidth);
            pixelHeight = Math.Max(1, pixelHeight);
            if (pixelWidth != _width || pixelHeight != _height)
            {
                _ctx.Target = null;
                _target?.Dispose();
                _target = null;
                _swapChain.ResizeBuffers(2, (uint)pixelWidth, (uint)pixelHeight, SwapChainFormat, SwapChainFlags.None).CheckError();
                _width = pixelWidth;
                _height = pixelHeight;
                CreateTarget();
            }
            using var sc2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            var inverse = Matrix3x2.CreateScale(1f / compositionScaleX, 1f / compositionScaleY);
            sc2.MatrixTransform = inverse;
        }
    }

    public void SetDisplay(DisplayColorInfo display)
    {
        lock (_gate)
        {
            if (Equals(display, _display)) return;
            _display = display;
            RebuildPipeline();
        }
    }

    /// <summary>
    /// 表示するコンテンツを設定する。source の所有権は呼び出し側に残る（このクラスは破棄しない）。
    /// upright は元のピクセル座標 → 正しい向きの座標 の変換（EXIF回転・動画の回転）。
    /// </summary>
    public void SetContent(ID2D1Image? source, SourceColor color, Matrix3x2 upright)
    {
        lock (_gate)
        {
            _source = source;
            _sourceColor = color;
            _upright = upright;
            RebuildPipeline();
        }
    }

    /// <summary>表示変換を設定する。scale はスワップチェーン1ピクセルあたりのコンテンツピクセル比、offset はピクセル単位。</summary>
    public void SetView(float scale, float offsetX, float offsetY)
    {
        lock (_gate)
        {
            _viewScale = scale;
            _view = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offsetX, offsetY);
        }
    }

    public void Render()
    {
        lock (_gate)
        {
            if (_disposed || _swapChain is null || _target is null) return;
            RenderCore();
        }
    }

    /// <summary>ロックを保持したまま追加の処理（動画フレームのコピー等）を行ってから描画する。</summary>
    public void RenderWith(Action beforeDraw)
    {
        lock (_gate)
        {
            if (_disposed || _swapChain is null || _target is null) return;
            beforeDraw();
            RenderCore();
        }
    }

    private void RenderCore()
    {
        if (IsDeviceLost) return;
        if (TryDraw()) return;
        if (IsDeviceLost) return;

        // ICCに起因する描画失敗の可能性がある場合は、明示したフォールバック（sRGB）で再試行する（仕様 §5.2-6）
        var retried = false;
        if (_sourceColor.Kind == SourceColorKind.EmbeddedIcc)
        {
            _sourceColor = new SourceColor(SourceColorKind.InvalidIccFallbackSrgb, null, "描画時にICCを適用できませんでした");
            retried = true;
        }
        if (_display.MonitorIccProfile is not null)
        {
            _display = _display with { MonitorIccProfile = null, Description = _display.Description + "（モニターICC適用失敗）" };
            retried = true;
        }
        if (!retried) return;
        RebuildPipeline();
        TryDraw();
    }

    private bool TryDraw()
    {
        var white = _display.SdrWhiteScale;
        var bg = BackgroundLinear * white;
        _ctx.Target = _target;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.Clear(new Color4(bg, bg, bg, 1f));
        if (_pipeline is not null)
        {
            _ctx.Transform = _upright * _view;
            _ctx.DrawImage(_pipeline.Output, null, null, ChooseInterpolation(_viewScale), CompositeMode.SourceOver);
            _ctx.Transform = Matrix3x2.Identity;
        }
        var hr = _ctx.EndDraw();
        if (hr.Failure)
        {
            // デバイス消失なら作り直しを依頼する。それ以外（エフェクト失敗等）は呼び出し側でフォールバックする
            CheckDeviceLost(hr.Code);
            return false;
        }
        var present = _swapChain!.Present(1, PresentFlags.None);
        if (present.Failure)
        {
            CheckDeviceLost(present.Code);
            return false;
        }
        return true;
    }

    /// <summary>
    /// GPUのリセット・ドライバー更新などでデバイスが失われた状態。以後の描画は行わず、
    /// <see cref="DeviceLost"/> を受けた側がデバイスを作り直す（仕様 §15：クラッシュさせない）。
    /// </summary>
    public bool IsDeviceLost { get; private set; }

    /// <summary>デバイス消失を検出した（描画を呼んだスレッドで1回だけ発生）。</summary>
    public event EventHandler? DeviceLost;

    private const int D2DErrRecreateTarget = unchecked((int)0x8899000C);
    private const int DxgiErrDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrDriverInternal = unchecked((int)0x887A0020);

    private void CheckDeviceLost(int hr)
    {
        var lost = hr is D2DErrRecreateTarget or DxgiErrDeviceRemoved or DxgiErrDeviceReset or DxgiErrDriverInternal
            || _device.D3DDevice.DeviceRemovedReason.Failure;
        if (lost) MarkDeviceLost();
    }

    private void MarkDeviceLost()
    {
        if (IsDeviceLost) return;
        IsDeviceLost = true;
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 解放済みの画像のGPUメモリを、ドライバーにすぐ返させる。表示する画像を切り替えた後に呼ぶ。
    /// Direct3D は解放したテクスチャを再利用のため保持し続けることがあり、大きな画像を
    /// 切り替えるとメモリが数百MB単位で残るため（仕様 §11.1：不要な資源を解放）。
    /// </summary>
    public void TrimMemory()
    {
        lock (_gate)
        {
            if (_disposed || IsDeviceLost) return;
            _device.D2DDevice.ClearResources(0);
            var immediate = _device.D3DDevice.ImmediateContext;
            immediate.ClearState();
            immediate.Flush();
            using var dxgi3 = _device.DxgiDevice.QueryInterfaceOrNull<IDXGIDevice3>();
            dxgi3?.Trim();
        }
    }

    /// <summary>デバイス消失からの復旧を検証するため、消失を模擬する（開発時の確認用）。</summary>
    public void SimulateDeviceLost()
    {
        lock (_gate) MarkDeviceLost();
    }

    /// <summary>縮小時は高品質、等倍付近は通常、大きく拡大したらピクセルを確認しやすい最近傍補間にする。</summary>
    private static InterpolationMode ChooseInterpolation(float scale) => scale switch
    {
        < 1f => InterpolationMode.HighQualityCubic,
        < 4f => InterpolationMode.Cubic,
        _ => InterpolationMode.NearestNeighbor,
    };

    private void RebuildPipeline()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        if (_source is null) return;
        _pipeline = ColorPipeline.Create(_ctx, _device.WicFactory, _source, _sourceColor, _display);
    }

    private void CreateTarget()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        _target = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(SwapChainFormat, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pipeline?.Dispose();
            _ctx.Target = null;
            _target?.Dispose();
            _swapChain?.Dispose();
            _ctx.Dispose();
        }
    }
}
