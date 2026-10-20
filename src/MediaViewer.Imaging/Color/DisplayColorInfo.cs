namespace MediaViewer.Imaging.Color;

/// <summary>表示先モニターの色の扱い（Windows の Advanced Color 状態）。</summary>
public enum DisplayColorMode
{
    /// <summary>通常のSDR。OSは色管理しないため、アプリがモニターICCへ変換する。</summary>
    Sdr,

    /// <summary>SDRの自動色管理（ACM）。OSがscRGBからモニターへ変換する。</summary>
    WideColorGamut,

    /// <summary>HDR。OSがscRGBからモニターへ変換する。SDR白レベルの補正が必要。</summary>
    Hdr,
}

/// <summary>
/// 色変換の出力先を決めるための表示先情報。
/// </summary>
/// <param name="Mode">Advanced Color の状態。</param>
/// <param name="SdrWhiteLevelNits">HDR時にSDRの白として使う輝度（nit）。scRGB 1.0 = 80 nit。</param>
/// <param name="MonitorIccProfile">SDR時のモニターICCプロファイル。取得できない場合は null（sRGBモニターとみなす）。</param>
/// <param name="Description">ログ・診断表示用の説明。</param>
public sealed record DisplayColorInfo(
    DisplayColorMode Mode,
    float SdrWhiteLevelNits,
    byte[]? MonitorIccProfile,
    string Description)
{
    public const float ScRgbReferenceWhiteNits = 80f;

    /// <summary>モニター情報が得られないときの明示的なフォールバック：sRGBのSDRモニター。</summary>
    public static DisplayColorInfo FallbackSrgb { get; } =
        new(DisplayColorMode.Sdr, ScRgbReferenceWhiteNits, null, "フォールバック（sRGBのSDRモニターとして扱う）");

    /// <summary>SDRコンテンツの白をscRGBで表す値（SDR/ACMでは1.0、HDRではSDR白レベル/80）。</summary>
    public float SdrWhiteScale =>
        Mode == DisplayColorMode.Hdr && SdrWhiteLevelNits > 0 ? SdrWhiteLevelNits / ScRgbReferenceWhiteNits : 1f;
}
