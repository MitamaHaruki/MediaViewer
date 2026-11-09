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

    // クロスフェード：切り替え前の画面の写し（_fadeFrom）を、不透明度を下げながら新しい画面の上に重ねる。
    // _fadeTo は新しい画面の写しで、大きな画像の色変換をフェード中に毎フレームやり直さないためのもの。
    private ID2D1Bitmap1? _fadeFrom;
    private ID2D1Bitmap1? _fadeTo;
    private float _fadeFromOpacity;

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
                // 写しの大きさが合わなくなるため、フェードはやめて新しい画面をそのまま出す
                ReleaseCrossFade();
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
            InvalidateFadeTarget();
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
            InvalidateFadeTarget();
        }
    }

    /// <summary>表示変換を設定する。scale はスワップチェーン1ピクセルあたりのコンテンツピクセル比、offset はピクセル単位。</summary>
    public void SetView(float scale, float offsetX, float offsetY)
    {
        lock (_gate)
        {
            var view = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offsetX, offsetY);
            if (view != _view) InvalidateFadeTarget();
            _viewScale = scale;
            _view = view;
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
        _ctx.Target = _target;
        _ctx.BeginDraw();
        DrawScene(includeFade: true);
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

    /// <summary>今の描画先へ、背景・コンテンツ（と、フェード中なら前の画面の写し）を描く。BeginDraw／EndDraw は呼び出し側で行う。</summary>
    private void DrawScene(bool includeFade)
    {
        _ctx.Transform = Matrix3x2.Identity;
        var bg = BackgroundLinear * _display.SdrWhiteScale;
        _ctx.Clear(new Color4(bg, bg, bg, 1f));
        if (includeFade && _fadeTo is not null)
        {
            _ctx.DrawBitmap(_fadeTo, 1f, BitmapInterpolationMode.NearestNeighbor);
        }
        else if (_pipeline is not null)
        {
            _ctx.Transform = _upright * _view;
            _ctx.DrawImage(_pipeline.Output, null, null, ChooseInterpolation(_viewScale), CompositeMode.SourceOver);
            _ctx.Transform = Matrix3x2.Identity;
        }
        if (includeFade && _fadeFrom is not null && _fadeFromOpacity > 0f)
        {
            // 写しは全面が不透明なので、重ねた結果は「前の画面×不透明度＋新しい画面×（1−不透明度）」になる（線形の光の量で混ぜる）
            _ctx.DrawBitmap(_fadeFrom, _fadeFromOpacity, BitmapInterpolationMode.NearestNeighbor);
        }
    }

    // ---- クロスフェード ----

    /// <summary>
    /// クロスフェードの始まり：今の画面を写し取り、<see cref="EndCrossFade"/> まで新しい画面の上に重ねて描く。
    /// 重ねる不透明度の初期値は 1（前の画面のまま）なので、次の画像を読み込んでいる間も前の画面が見え続ける。
    /// 写し取れなかった場合は false（フェードせずに切り替える）。
    /// </summary>
    public bool BeginCrossFade()
    {
        lock (_gate)
        {
            if (_disposed || _swapChain is null || _target is null || IsDeviceLost) return false;
            var snapshot = CreateFrameBitmap();
            if (!DrawTo(snapshot, includeFade: true))
            {
                snapshot.Dispose();
                return false;
            }
            ReleaseCrossFade();
            _fadeFrom = snapshot;
            _fadeFromOpacity = 1f;
            return true;
        }
    }

    /// <summary>
    /// 前の画面を重ねる不透明度（1 = 前の画面、0 = 新しい画面）。初めて呼んだときに新しい画面も写し取り、
    /// 以後は2枚の写しを重ねるだけにする。
    /// </summary>
    public void SetCrossFade(float fromOpacity)
    {
        lock (_gate)
        {
            if (_fadeFrom is null) return;
            _fadeFromOpacity = Math.Clamp(fromOpacity, 0f, 1f);
            if (_fadeTo is not null || _disposed || _target is null || IsDeviceLost) return;
            var next = CreateFrameBitmap();
            if (DrawTo(next, includeFade: false)) _fadeTo = next;
            else next.Dispose();
        }
    }

    /// <summary>クロスフェードを終え、写しを解放する。</summary>
    public void EndCrossFade()
    {
        lock (_gate) ReleaseCrossFade();
    }

    private ID2D1Bitmap1 CreateFrameBitmap() =>
        _ctx.CreateBitmap(new SizeI(_width, _height), IntPtr.Zero, 0, new BitmapProperties1(
            new PixelFormat(SwapChainFormat, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96,
            BitmapOptions.Target));

    private bool DrawTo(ID2D1Bitmap1 bitmap, bool includeFade)
    {
        _ctx.Target = bitmap;
        _ctx.BeginDraw();
        DrawScene(includeFade);
        var hr = _ctx.EndDraw();
        _ctx.Target = _target;
        if (hr.Failure)
        {
            CheckDeviceLost(hr.Code);
            return false;
        }
        return true;
    }

    /// <summary>表示内容・倍率・表示先が変わったら、新しい画面の写しを作り直す（次の <see cref="SetCrossFade"/> で）。</summary>
    private void InvalidateFadeTarget()
    {
        _fadeTo?.Dispose();
        _fadeTo = null;
    }

    private void ReleaseCrossFade()
    {
        _fadeFrom?.Dispose();
        _fadeFrom = null;
        InvalidateFadeTarget();
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
            ReleaseCrossFade();
            _pipeline?.Dispose();
            _ctx.Target = null;
            _target?.Dispose();
            _swapChain?.Dispose();
            _ctx.Dispose();
        }
    }
}
