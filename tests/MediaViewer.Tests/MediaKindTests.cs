using MediaViewer.Core.Media;

namespace MediaViewer.Tests;

[TestClass]
public sealed class MediaKindTests
{
    [TestMethod]
    [DataRow(@"C:\a\photo.JPG", MediaKind.Image)]
    [DataRow(@"C:\a\写真 🎉.webp", MediaKind.Image)]
    [DataRow(@"C:\a\movie.MkV", MediaKind.Video)]
    [DataRow(@"C:\a\song.flac", MediaKind.Audio)]
    [DataRow(@"C:\a\notes.txt", MediaKind.Unknown)]
    [DataRow(@"C:\a\noext", MediaKind.Unknown)]
    public void 拡張子から種別を判定する(string path, MediaKind expected)
    {
        Assert.AreEqual(expected, MediaKinds.FromPath(path));
    }
}
