using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.WIC;

namespace MediaViewer.Imaging.Color;

/// <summary>
/// ソース色空間 → 表示先 への色変換を Direct2D のエフェクトグラフとして組み立てる。出力は常に scRGB（線形・sRGB原色）。
/// <list type="bullet">
/// <item>HDR / ACM：ソース → scRGB。OSがモニターへ変換するため、アプリはモニターICCを適用しない（二重変換防止）。HDRではSDR白レベルへ補正する。</item>
/// <item>SDR：ソース → モニターICC。その値をOSが sRGB として解釈して出力するため、sRGB → scRGB で包み直す。</item>
/// </list>
/// レンダリング意図は相対的な色域を維持する（RelativeColorimetric）で固定し、色域外は出力時にクリップされる（仕様 §5.3）。
/// </summary>
public sealed class ColorPipeline : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public ID2D1Image Output { get; private set; } = null!;

    /// <summary>診断用の説明（どの経路で変換しているか）。</summary>
    public string Description { get; private set; } = "";

    /// <summary>ソースICCが使えずsRGBにフォールバックしたか。</summary>
    public bool SourceFellBack { get; private set; }

    /// <summary>モニターICCが使えずsRGBモニターとみなしたか。</summary>
    public bool DisplayFellBack { get; private set; }

    private ColorPipeline() { }

    public static ColorPipeline Create(
        ID2D1DeviceContext5 ctx,
        IWICImagingFactory wic,
        ID2D1Image source,
        SourceColor sourceColor,
        DisplayColorInfo display)
    {
        var p = new ColorPipeline();
        try
        {
            p.Build(ctx, wic, source, sourceColor, display);
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    private void Build(ID2D1DeviceContext5 ctx, IWICImagingFactory wic, ID2D1Image source, SourceColor sourceColor, DisplayColorInfo display)
    {
        var srcContext = Own(CreateSourceContext(ctx, wic, sourceColor, out var sourceLabel));
        var scRgb = Own(ctx.CreateColorContext(ColorSpace.ScRgb, null!, 0));

        if (display.Mode == DisplayColorMode.Sdr)
        {
            var monitor = TryCreateMonitorContext(ctx, display.MonitorIccProfile);
            if (monitor is not null)
            {
                Own(monitor);
                var toMonitor = CreateColorManagement(ctx, source, srcContext, monitor);
                var srgb = Own(ctx.CreateColorContext(ColorSpace.Srgb, null!, 0));
                var rewrap = CreateColorManagement(ctx, toMonitor.Output, srgb, scRgb);
                Output = rewrap.Output;
                Description = $"{sourceLabel} → モニターICC（SDR）";
            }
            else
            {
                DisplayFellBack = display.MonitorIccProfile is not null;
                var cm = CreateColorManagement(ctx, source, srcContext, scRgb);
                Output = cm.Output;
                Description = $"{sourceLabel} → sRGBモニター想定（SDR、モニターICCなし{(DisplayFellBack ? "：ICC不正" : "")}）";
            }
            return;
        }

        var toScRgb = CreateColorManagement(ctx, source, srcContext, scRgb);
        if (display.Mode == DisplayColorMode.Hdr && Math.Abs(display.SdrWhiteScale - 1f) > 1e-3f)
        {
            var white = Own(new ID2D1Effect(ctx.CreateEffect(EffectGuids.WhiteLevelAdjustment)));
            white.SetInput(0, toScRgb.Output, true);
            // 入力の1.0をSDR白レベル（nit）として扱い、出力の1.0 = 80 nit（scRGB基準）へ換算する
            white.SetValue((uint)WhiteLevelAdjustmentProperties.InputWhiteLevel, display.SdrWhiteLevelNits);
            white.SetValue((uint)WhiteLevelAdjustmentProperties.OutputWhiteLevel, DisplayColorInfo.ScRgbReferenceWhiteNits);
            Output = white.Output;
        }
        else
        {
            Output = toScRgb.Output;
        }
        Description = display.Mode == DisplayColorMode.Hdr
            ? $"{sourceLabel} → scRGB（HDR、SDR白 {display.SdrWhiteLevelNits:0} nit）"
            : $"{sourceLabel} → scRGB（自動色管理）";
    }

    private ID2D1ColorContext CreateSourceContext(ID2D1DeviceContext5 ctx, IWICImagingFactory wic, SourceColor color, out string label)
    {
        switch (color.Kind)
        {
            case SourceColorKind.EmbeddedIcc when color.IccProfile is { Length: > 0 } icc:
                if (!SourceColor.IsSupportedRgbIcc(icc, out _))
                {
                    SourceFellBack = true;
                    label = "ICC不正→sRGB";
                    return ctx.CreateColorContext(ColorSpace.Srgb, null!, 0);
                }
                try
                {
                    var c = ctx.CreateColorContext(ColorSpace.Custom, icc, (uint)icc.Length);
                    label = "埋め込みICC";
                    return c;
                }
                catch (SharpGenException)
                {
                    // 解釈できないICC：クラッシュさせず sRGB として表示する（仕様 §5.2-6）
                    SourceFellBack = true;
                    label = "ICC解釈不可→sRGB";
                    return ctx.CreateColorContext(ColorSpace.Srgb, null!, 0);
                }
            case SourceColorKind.ExifAdobeRgb:
                {
                    using var wicContext = wic.CreateColorContext();
                    wicContext.InitializeFromExifColorSpace(2);
                    label = "EXIF Adobe RGB";
                    return ctx.CreateColorContextFromWicColorContext(wicContext);
                }
            case SourceColorKind.InvalidIccFallbackSrgb:
                SourceFellBack = true;
                label = "ICC不正→sRGB";
                return ctx.CreateColorContext(ColorSpace.Srgb, null!, 0);
            case SourceColorKind.ExifSrgb:
                label = "EXIF sRGB";
                return ctx.CreateColorContext(ColorSpace.Srgb, null!, 0);
            default:
                label = "sRGB（指定なし）";
                return ctx.CreateColorContext(ColorSpace.Srgb, null!, 0);
        }
    }

    private static ID2D1ColorContext? TryCreateMonitorContext(ID2D1DeviceContext5 ctx, byte[]? icc)
    {
        if (icc is not { Length: > 0 } || !SourceColor.IsSupportedRgbIcc(icc, out _)) return null;
        try
        {
            return ctx.CreateColorContext(ColorSpace.Custom, icc, (uint)icc.Length);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private ID2D1Effect CreateColorManagement(ID2D1DeviceContext5 ctx, ID2D1Image input, ID2D1ColorContext from, ID2D1ColorContext to)
    {
        var e = Own(new ID2D1Effect(ctx.CreateEffect(EffectGuids.ColorManagement)));
        e.SetInput(0, input, true);
        e.SetValue((uint)ColorManagementProperties.SourceColorContext, from);
        e.SetValue((uint)ColorManagementProperties.DestinationColorContext, to);
        SetEnum(e, (uint)ColorManagementProperties.SourceRenderingIntent, (uint)ColorManagementRenderingIntent.RelativeColorimetric);
        SetEnum(e, (uint)ColorManagementProperties.DestinationRenderingIntent, (uint)ColorManagementRenderingIntent.RelativeColorimetric);
        SetEnum(e, (uint)ColorManagementProperties.AlphaMode, (uint)ColorManagementAlphaMode.Premultiplied);
        SetEnum(e, (uint)ColorManagementProperties.Quality, (uint)ColormanagementQuality.Best);
        return e;
    }

    private static unsafe void SetEnum(ID2D1Effect effect, uint index, uint value) =>
        effect.SetValue(index, PropertyType.Enum, &value, sizeof(uint));

    private T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
