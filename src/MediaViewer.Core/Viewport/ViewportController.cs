namespace MediaViewer.Core.Viewport;

/// <summary>
/// 画像・動画で共通の表示状態（倍率・位置）を管理する。
/// 座標系：ビューポートはDIP、コンテンツは素材のピクセル（EXIF回転・動画アスペクト比を適用済みの寸法）。
/// 画面座標 = コンテンツ座標 × <see cref="Scale"/> + (<see cref="OffsetX"/>, <see cref="OffsetY"/>)。
/// </summary>
public sealed class ViewportController
{
    /// <summary>最大倍率（等倍に対する倍数）。</summary>
    public const double MaxZoomRelativeToActualSize = 32.0;

    /// <summary>等倍がフィット以下のとき、ダブルクリックで拡大する倍数（フィット基準）。</summary>
    public const double ToggleZoomFallbackFactor = 2.0;

    private const double FitTolerance = 1e-6;

    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }

    /// <summary>1 DIP あたりの物理ピクセル数（DPIスケール）。等倍の算出に使う。</summary>
    public double PixelsPerDip { get; private set; } = 1.0;

    /// <summary>true のとき、表示領域より小さいコンテンツは等倍を上限としてフィットする（画像用）。</summary>
    public bool LimitFitToActualSize { get; private set; }

    /// <summary>フィット時の表示サイズの上限（DIP、長辺）。アルバムアートの表示枠などに使う。</summary>
    public double MaxFitExtent { get; private set; } = double.PositiveInfinity;

    public double FitScale { get; private set; }
    public double Scale { get; private set; }
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public bool HasContent => ContentWidth > 0 && ContentHeight > 0;
    public bool HasViewport => ViewportWidth > 0 && ViewportHeight > 0;

    /// <summary>素材の1ピクセルが物理1ピクセルになる倍率。</summary>
    public double ActualSizeScale => 1.0 / PixelsPerDip;

    public double MinScale => FitScale;
    public double MaxScale => Math.Max(FitScale, ActualSizeScale * MaxZoomRelativeToActualSize);

    public bool IsAtFit => !HasContent || Math.Abs(Scale - FitScale) <= FitScale * FitTolerance;

    /// <summary>拡大によりコンテンツが表示領域からはみ出しているか（パン可能か）。</summary>
    public bool CanPan =>
        HasContent && (ContentWidth * Scale > ViewportWidth + 0.5 || ContentHeight * Scale > ViewportHeight + 0.5);

    public event EventHandler? Changed;

    public void SetContent(double width, double height, bool limitFitToActualSize, double maxFitExtent = double.PositiveInfinity)
    {
        ContentWidth = Math.Max(0, width);
        ContentHeight = Math.Max(0, height);
        LimitFitToActualSize = limitFitToActualSize;
        MaxFitExtent = maxFitExtent > 0 ? maxFitExtent : double.PositiveInfinity;
        RecalculateFitScale();
        ApplyFit();
        RaiseChanged();
    }

    public void ClearContent()
    {
        ContentWidth = ContentHeight = 0;
        FitScale = Scale = 0;
        OffsetX = OffsetY = 0;
        RaiseChanged();
    }

    public void SetViewport(double width, double height, double pixelsPerDip)
    {
        var wasAtFit = IsAtFit;
        var oldCenterX = ViewportWidth / 2;
        var oldCenterY = ViewportHeight / 2;
        var oldScale = Scale;

        ViewportWidth = Math.Max(0, width);
        ViewportHeight = Math.Max(0, height);
        PixelsPerDip = pixelsPerDip > 0 ? pixelsPerDip : 1.0;
        RecalculateFitScale();

        if (!HasContent)
        {
            RaiseChanged();
            return;
        }

        if (wasAtFit || oldScale <= 0)
        {
            ApplyFit();
        }
        else
        {
            // 拡大中は、以前の表示中心にあったコンテンツ点を新しい中心に保つ
            var contentX = (oldCenterX - OffsetX) / oldScale;
            var contentY = (oldCenterY - OffsetY) / oldScale;
            Scale = Math.Clamp(oldScale, MinScale, MaxScale);
            OffsetX = ViewportWidth / 2 - contentX * Scale;
            OffsetY = ViewportHeight / 2 - contentY * Scale;
            ClampOffsets();
        }
        RaiseChanged();
    }

    public void ResetToFit()
    {
        if (!HasContent) return;
        ApplyFit();
        RaiseChanged();
    }

    /// <summary>指定点（ビューポート座標）を中心に倍率を factor 倍する。指定点の下のコンテンツ点は動かない。</summary>
    public void ZoomAt(double anchorX, double anchorY, double factor)
    {
        if (!HasContent || !HasViewport || factor <= 0 || double.IsNaN(factor)) return;
        ZoomTo(Scale * factor, anchorX, anchorY);
    }

    public void ZoomTo(double scale, double anchorX, double anchorY)
    {
        if (!HasContent || !HasViewport || Scale <= 0) return;
        var newScale = Math.Clamp(scale, MinScale, MaxScale);
        var contentX = (anchorX - OffsetX) / Scale;
        var contentY = (anchorY - OffsetY) / Scale;
        Scale = newScale;
        OffsetX = anchorX - contentX * newScale;
        OffsetY = anchorY - contentY * newScale;
        ClampOffsets();
        RaiseChanged();
    }

    public void Pan(double deltaX, double deltaY)
    {
        if (!HasContent || !CanPan) return;
        OffsetX += deltaX;
        OffsetY += deltaY;
        ClampOffsets();
        RaiseChanged();
    }

    /// <summary>ダブルクリック／ダブルタップ：フィット ↔ 等倍（等倍がフィット以下ならフィット×2）。</summary>
    public void ToggleZoom(double anchorX, double anchorY)
    {
        if (!HasContent) return;
        if (IsAtFit)
        {
            var target = ActualSizeScale > FitScale * (1 + 1e-3)
                ? ActualSizeScale
                : FitScale * ToggleZoomFallbackFactor;
            ZoomTo(target, anchorX, anchorY);
        }
        else
        {
            ResetToFit();
        }
    }

    private void RecalculateFitScale()
    {
        if (!HasContent || !HasViewport)
        {
            FitScale = HasContent ? ActualSizeScale : 0;
            return;
        }
        var fit = Math.Min(ViewportWidth / ContentWidth, ViewportHeight / ContentHeight);
        fit = Math.Min(fit, MaxFitExtent / Math.Max(ContentWidth, ContentHeight));
        FitScale = LimitFitToActualSize ? Math.Min(fit, ActualSizeScale) : fit;
    }

    private void ApplyFit()
    {
        Scale = FitScale;
        OffsetX = (ViewportWidth - ContentWidth * Scale) / 2;
        OffsetY = (ViewportHeight - ContentHeight * Scale) / 2;
    }

    private void ClampOffsets()
    {
        OffsetX = ClampAxis(OffsetX, ContentWidth * Scale, ViewportWidth);
        OffsetY = ClampAxis(OffsetY, ContentHeight * Scale, ViewportHeight);
    }

    /// <summary>コンテンツが表示領域より小さい軸は中央寄せ、大きい軸は端に余白ができない範囲に制限する。</summary>
    private static double ClampAxis(double offset, double scaledSize, double viewportSize)
    {
        if (scaledSize <= viewportSize) return (viewportSize - scaledSize) / 2;
        return Math.Clamp(offset, viewportSize - scaledSize, 0);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
