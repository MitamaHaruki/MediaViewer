using System.Runtime.InteropServices;

namespace MediaViewer.Core.Navigation;

/// <summary>
/// エクスプローラーと同じ自然順（file2 → file10、大文字小文字を区別しない）でファイル名を比較する。
/// Windows の StrCmpLogicalW を使い、エクスプローラーの並びと一致させる。
/// </summary>
public sealed partial class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return StrCmpLogicalW(x, y);
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string psz1, string psz2);
}
