using MediaViewer.Core.Media;

namespace MediaViewer.Core.Navigation;

/// <summary>前後移動の対象を絞り込むフィルター（仕様 §4.2）。</summary>
public enum NavigationFilter
{
    All,
    Images,
    Videos,
    Audio,
}

public readonly record struct NavigationNeighbors(string? Previous, string? Next)
{
    public static NavigationNeighbors None => default;
}

/// <summary>
/// 現在のファイルと同じフォルダ直下の対応メディアから、前後のファイルを求める（仕様 §4）。
/// 一覧はファイルパスだけを扱い、画像やメタデータは読まない。
/// フォルダは呼び出しのたびに列挙し直し、追加・削除されたファイルを反映する（仕様決定事項 #9）。
/// </summary>
public static class FolderNavigator
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        MatchType = MatchType.Simple,
    };

    public static bool Matches(NavigationFilter filter, MediaKind kind) => filter switch
    {
        NavigationFilter.Images => kind == MediaKind.Image,
        NavigationFilter.Videos => kind == MediaKind.Video,
        NavigationFilter.Audio => kind == MediaKind.Audio,
        _ => kind != MediaKind.Unknown,
    };

    /// <summary>フォルダを列挙して前後を求める。フォルダにアクセスできない場合は前後なしを返す。</summary>
    public static NavigationNeighbors FindNeighbors(string currentPath, NavigationFilter filter)
    {
        var directory = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(directory)) return NavigationNeighbors.None;

        List<string> names;
        try
        {
            names = Directory.EnumerateFiles(directory, "*", Enumeration)
                .Select(Path.GetFileName)
                .Where(n => n is not null && MediaKinds.IsSupported(n))
                .ToList()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return NavigationNeighbors.None;
        }

        var (prev, next) = FindNeighbors(names, Path.GetFileName(currentPath), filter, NaturalStringComparer.Instance);
        return new NavigationNeighbors(
            prev is null ? null : Path.Combine(directory, prev),
            next is null ? null : Path.Combine(directory, next));
    }

    /// <summary>
    /// ファイル名一覧から前後を求める。現在のファイルが一覧にない（削除済み・フィルター対象外）場合も、
    /// 名前順でその位置にあるものとして前後を探す（仕様 §4.3-2）。末尾・先頭で循環しない（§4.3-3）。
    /// </summary>
    public static (string? Previous, string? Next) FindNeighbors(
        IEnumerable<string> fileNames, string currentName, NavigationFilter filter, IComparer<string> comparer)
    {
        var sorted = fileNames.ToList();
        sorted.Sort(comparer);
        var index = sorted.BinarySearch(currentName, comparer);
        int nextStart, prevStart;
        if (index >= 0)
        {
            nextStart = index + 1;
            prevStart = index - 1;
        }
        else
        {
            var insertion = ~index;
            nextStart = insertion;
            prevStart = insertion - 1;
        }

        string? next = null;
        for (var i = nextStart; i < sorted.Count; i++)
        {
            if (Matches(filter, MediaKinds.FromPath(sorted[i]))) { next = sorted[i]; break; }
        }
        string? prev = null;
        for (var i = prevStart; i >= 0; i--)
        {
            if (Matches(filter, MediaKinds.FromPath(sorted[i]))) { prev = sorted[i]; break; }
        }
        return (prev, next);
    }
}
