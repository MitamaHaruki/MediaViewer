using MediaViewer.Imaging.Color;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace MediaViewer.App.Services;

/// <summary>
/// ウィンドウが表示されているモニターの色情報（HDR/ACM状態・SDR白レベル・ICC）を監視する。
/// モニター間の移動、HDR切替、ICC変更で <see cref="Changed"/> を発生させる（仕様 §5.2-5）。
/// </summary>
internal sealed class DisplayColorMonitor : IDisposable
{
    private readonly AppWindow _window;
    private readonly DisplayInformation _info;
    private DisplayId _lastDisplay;

    public event EventHandler? Changed;

    public DisplayColorMonitor(AppWindow window)
    {
        _window = window;
        _info = DisplayInformation.CreateForWindowId(window.Id);
        _info.AdvancedColorInfoChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _info.ColorProfileChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _window.Changed += OnWindowChanged;
        _lastDisplay = CurrentDisplayId();
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange) return;
        var current = CurrentDisplayId();
        if (current.Value == _lastDisplay.Value) return;
        _lastDisplay = current;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DisplayId CurrentDisplayId()
    {
        try
        {
            return DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Nearest).DisplayId;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>現在の表示先情報。取得に失敗した場合は sRGB の SDR モニターとして扱う（仕様 §5.3）。</summary>
    public DisplayColorInfo Read()
    {
        try
        {
            var aci = _info.GetAdvancedColorInfo();
            var mode = aci.CurrentAdvancedColorKind switch
            {
                DisplayAdvancedColorKind.HighDynamicRange => DisplayColorMode.Hdr,
                DisplayAdvancedColorKind.WideColorGamut => DisplayColorMode.WideColorGamut,
                _ => DisplayColorMode.Sdr,
            };
            var white = aci.SdrWhiteLevelInNits > 0 ? (float)aci.SdrWhiteLevelInNits : DisplayColorInfo.ScRgbReferenceWhiteNits;
            byte[]? icc = mode == DisplayColorMode.Sdr ? ReadProfile() : null;
            var description = mode switch
            {
                DisplayColorMode.Hdr => $"HDR（SDR白 {white:0} nit、最大 {aci.MaxLuminanceInNits:0} nit）",
                DisplayColorMode.WideColorGamut => "SDR・自動色管理（ACM）",
                _ => icc is null ? "SDR（モニターICCなし→sRGB想定）" : $"SDR（モニターICC {icc.Length:N0} バイト）",
            };
            return new DisplayColorInfo(mode, white, icc, description);
        }
        catch (Exception)
        {
            return DisplayColorInfo.FallbackSrgb;
        }
    }

    private byte[]? ReadProfile()
    {
        try
        {
            using var stream = _info.GetColorProfile();
            if (stream is null || stream.Size == 0) return null;
            using var s = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _window.Changed -= OnWindowChanged;
        _info.Dispose();
    }
}
