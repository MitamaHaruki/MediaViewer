namespace MediaViewer.Core.Conversion;

/// <summary>変換の出力ファイル名（仕様 §9.1）。</summary>
public static class ConversionPaths
{
    /// <summary>既定の出力先：元ファイルと同じフォルダで、拡張子だけを変えた名前。</summary>
    public static string Suggest(string inputPath, ConversionFormat target, string? folder = null)
    {
        var dir = folder ?? Path.GetDirectoryName(inputPath) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(inputPath) + target.Extension());
    }

    /// <summary>同名のファイルがあれば「名前 (2).拡張子」のような連番の名前にする。</summary>
    public static string MakeUnique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException("連番の出力ファイル名を決められませんでした。");
    }

    /// <summary>元ファイルと同じ場所を指しているか（元ファイルは上書きしない、仕様 §9.1）。</summary>
    public static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 変換中に書き込む一時ファイル。完成してから最終名に置き換え、中断・失敗時に壊れた完成ファイルを残さない（仕様 §9.3）。
    /// 拡張子は出力形式と同じにする。
    /// </summary>
    public static string TemporaryFor(string finalPath)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(finalPath);
        return Path.Combine(dir, $"~{name}.converting-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(finalPath)}");
    }
}
