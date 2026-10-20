namespace MediaViewer.Imaging.Color;

/// <summary>画像の色空間をどこから決めたか。</summary>
public enum SourceColorKind
{
    /// <summary>埋め込みICCプロファイル。</summary>
    EmbeddedIcc,

    /// <summary>EXIF の色空間指定が sRGB。</summary>
    ExifSrgb,

    /// <summary>EXIF の色空間指定が Adobe RGB（ColorSpace=2、または未校正＋互換性インデックス R03）。</summary>
    ExifAdobeRgb,

    /// <summary>グレーの ICC（kTRC）から同じ階調の RGB プロファイルを作った。</summary>
    GrayIcc,

    /// <summary>CMYK を ICC で作業用の広色域 RGB へ変換した。</summary>
    CmykConverted,

    /// <summary>PNG の cICP（SDR の符号化指定）から ICC を作った。</summary>
    PngCicp,

    /// <summary>PNG の sRGB チャンク。</summary>
    PngSrgb,

    /// <summary>PNG の gAMA / cHRM から ICC を作った。</summary>
    PngGamma,

    /// <summary>HDR（PQ、BT.2020 原色）。PNG の cICP など。</summary>
    HdrPq,

    /// <summary>色空間の指定がないため sRGB とみなした（仕様 §5.2-2）。</summary>
    AssumedSrgb,

    /// <summary>ICC が不正・未対応のため sRGB にフォールバックした（仕様 §5.2-6）。</summary>
    InvalidIccFallbackSrgb,
}

/// <summary>
/// 画像（または動画フレーム）のソース色空間。IccProfile があればそれを使う。
/// HdrMaxNits は HDR コンテンツの最大輝度（不明なら 0）。
/// </summary>
public sealed record SourceColor(SourceColorKind Kind, byte[]? IccProfile, string? Note = null, float HdrMaxNits = 0)
{
    public static SourceColor Srgb { get; } = new(SourceColorKind.AssumedSrgb, null);

    public bool UsesFallback => Kind is SourceColorKind.InvalidIccFallbackSrgb;

    public bool IsHdr => Kind is SourceColorKind.HdrPq;

    /// <summary>診断表示用の名前。</summary>
    public string Label => Kind switch
    {
        SourceColorKind.EmbeddedIcc => "埋め込みICC",
        SourceColorKind.ExifSrgb => "EXIF sRGB",
        SourceColorKind.ExifAdobeRgb => "EXIF Adobe RGB",
        SourceColorKind.GrayIcc => "グレーICC",
        SourceColorKind.CmykConverted => "CMYK（ICCで変換）",
        SourceColorKind.PngCicp => "PNG cICP",
        SourceColorKind.PngSrgb => "PNG sRGB",
        SourceColorKind.PngGamma => "PNG gAMA/cHRM",
        SourceColorKind.HdrPq => "HDR（PQ / BT.2020）",
        SourceColorKind.InvalidIccFallbackSrgb => "ICC不正→sRGB",
        _ => "sRGB（指定なし）",
    };

    /// <summary>
    /// ICCヘッダーの最低限の検証。RGBのデバイス色空間を持つ表示・入力・色空間プロファイルのみ受け付ける。
    /// グレー・CMYK等は現在のパイプライン（RGBピクセル）と整合しないため不正扱いとする。
    /// </summary>
    public static bool IsSupportedRgbIcc(ReadOnlySpan<byte> icc, out string? reason)
    {
        reason = null;
        if (icc.Length < 132)
        {
            reason = "ICCプロファイルが短すぎます";
            return false;
        }
        var declaredSize = (icc[0] << 24) | (icc[1] << 16) | (icc[2] << 8) | icc[3];
        if (declaredSize > icc.Length || declaredSize < 132)
        {
            reason = "ICCプロファイルのサイズが不正です";
            return false;
        }
        if (!icc.Slice(36, 4).SequenceEqual("acsp"u8))
        {
            reason = "ICCプロファイルの署名が不正です";
            return false;
        }
        if (!icc.Slice(16, 4).SequenceEqual("RGB "u8))
        {
            reason = $"RGB以外のICCプロファイル（{System.Text.Encoding.ASCII.GetString(icc.Slice(16, 4)).Trim()}）は未対応です";
            return false;
        }
        var deviceClass = icc.Slice(12, 4);
        if (!(deviceClass.SequenceEqual("mntr"u8) || deviceClass.SequenceEqual("scnr"u8)
              || deviceClass.SequenceEqual("spac"u8) || deviceClass.SequenceEqual("prtr"u8)))
        {
            reason = "画像に使えない種類のICCプロファイルです";
            return false;
        }
        var pcs = icc.Slice(20, 4);
        if (!(pcs.SequenceEqual("XYZ "u8) || pcs.SequenceEqual("Lab "u8)))
        {
            reason = "ICCプロファイルのPCSが不正です";
            return false;
        }
        return HasValidTagTable(icc[..declaredSize], ref reason);
    }

    /// <summary>
    /// タグ表の構造検証。各タグがプロファイル内に収まり、RGB→PCS の変換に必要なタグ
    /// （行列＋トーンカーブ、または A2B LUT）が揃っていることを確認する。
    /// Direct2D は壊れたタグ表でもコンテキスト生成に成功する場合があるため、ここで弾く。
    /// </summary>
    private static bool HasValidTagTable(ReadOnlySpan<byte> icc, ref string? reason)
    {
        const int maxTags = 200;
        var count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(icc[128..]);
        var tableEnd = 132L + 12L * count;
        if (count is <= 0 or > maxTags || tableEnd > icc.Length)
        {
            reason = "ICCプロファイルのタグ表が不正です";
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var entry = icc.Slice(132 + 12 * i, 12);
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset < tableEnd || size < 8 || (long)offset + size > icc.Length)
            {
                reason = "ICCプロファイルのタグがファイル範囲外を指しています";
                return false;
            }
            found.Add(System.Text.Encoding.ASCII.GetString(entry[..4]));
        }

        var hasMatrixTrc = found.IsSupersetOf(["rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"]);
        var hasLut = found.Contains("A2B0") || found.Contains("A2B1");
        if (!hasMatrixTrc && !hasLut)
        {
            reason = "ICCプロファイルに色変換に必要なタグがありません";
            return false;
        }
        return true;
    }
}
