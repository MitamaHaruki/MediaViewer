using MediaViewer.Core.Navigation;

namespace MediaViewer.Tests;

[TestClass]
public sealed class FolderNavigatorTests
{
    private static readonly string[] Files =
    [
        "file10.png", "file2.jpg", "File1.mp4", "file3.mp3", "notes.txt", "file20.mkv", "写真 🎉.webp",
    ];

    private static (string? Prev, string? Next) Find(string current, NavigationFilter filter) =>
        FolderNavigator.FindNeighbors(Files, current, filter, NaturalStringComparer.Instance);

    [TestMethod]
    public void 自然順で大文字小文字を区別せずに並べる()
    {
        var sorted = Files.ToList();
        sorted.Sort(NaturalStringComparer.Instance);
        CollectionAssert.AreEqual(
            new[] { "File1.mp4", "file2.jpg", "file3.mp3", "file10.png", "file20.mkv", "notes.txt", "写真 🎉.webp" },
            sorted);
    }

    [TestMethod]
    public void フィルターなしでは対応メディアの前後を返す()
    {
        Assert.AreEqual(("file3.mp3", "file20.mkv"), Find("file10.png", NavigationFilter.All));
    }

    [TestMethod]
    public void 先頭と末尾では循環しない()
    {
        Assert.AreEqual((null, "file2.jpg"), Find("File1.mp4", NavigationFilter.All));
        Assert.AreEqual(("file20.mkv", null), Find("写真 🎉.webp", NavigationFilter.All));
    }

    [TestMethod]
    public void フィルターに一致するファイルだけを対象にする()
    {
        Assert.AreEqual(("file2.jpg", "写真 🎉.webp"), Find("file10.png", NavigationFilter.Images));
        Assert.AreEqual(("File1.mp4", "file20.mkv"), Find("file10.png", NavigationFilter.Videos));
        Assert.AreEqual(("file3.mp3", null), Find("file10.png", NavigationFilter.Audio));
    }

    [TestMethod]
    public void 現在のファイルがフィルター対象外でもその位置を起点に移動できる()
    {
        // 画像表示中に「動画のみ」へ切替
        Assert.AreEqual(("File1.mp4", "file20.mkv"), Find("file2.jpg", NavigationFilter.Videos));
    }

    [TestMethod]
    public void 現在のファイルが削除されていても名前順の位置から前後を求める()
    {
        Assert.AreEqual(("file3.mp3", "file10.png"), Find("file5.png", NavigationFilter.All));
    }

    [TestMethod]
    public void 実フォルダを列挙して前後を求める()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MediaViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in new[] { "a1.png", "a2.mp4", "a10.flac", "skip.txt" })
            {
                File.WriteAllBytes(Path.Combine(dir, f), []);
            }
            var hidden = Path.Combine(dir, "a3.png");
            File.WriteAllBytes(hidden, []);
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var n = FolderNavigator.FindNeighbors(Path.Combine(dir, "a2.mp4"), NavigationFilter.All);
            Assert.AreEqual(Path.Combine(dir, "a1.png"), n.Previous);
            Assert.AreEqual(Path.Combine(dir, "a10.flac"), n.Next, "隠しファイルは対象外");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void 存在しないフォルダでは前後なしを返す()
    {
        var n = FolderNavigator.FindNeighbors(@"Z:\存在しない\フォルダ\x.png", NavigationFilter.All);
        Assert.IsNull(n.Previous);
        Assert.IsNull(n.Next);
    }
}
