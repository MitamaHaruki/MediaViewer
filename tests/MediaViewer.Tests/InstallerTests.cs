using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaViewer.Core.Media;

namespace MediaViewer.Tests;

/// <summary>インストーラーの定義（installer/）がアプリの実装と食い違っていないかを確かめる。</summary>
[TestClass]
public sealed partial class InstallerTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [TestMethod]
    public void FileAssociations_MatchSupportedExtensions()
    {
        var doc = XDocument.Load(Path.Combine(FindRepositoryRoot(), "installer", "Associations.wxs"));
        var associations = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in doc.Descendants(Wix + "RegistryValue"))
        {
            var key = (string?)value.Attribute("Key") ?? "";
            var match = OpenWithProgidsKey().Match(key);
            if (!match.Success) continue;
            var progId = (string)value.Attribute("Name")!;
            Assert.IsTrue(Enum.TryParse<MediaKind>(progId["MediaViewer.".Length..], out var kind), progId);
            Assert.IsTrue(associations.TryAdd(match.Groups[1].Value, kind), $"重複：{match.Groups[1].Value}");
        }

        CollectionAssert.AreEquivalent(MediaKinds.Extensions.Keys.Select(k => k.ToLowerInvariant()).ToList(), associations.Keys.ToList());
        foreach (var (ext, kind) in associations)
        {
            Assert.AreEqual(MediaKinds.Extensions[ext], kind, ext);
        }

        // 既定のアプリの候補（Capabilities）とアプリの対応形式（SupportedTypes）にも同じ拡張子を登録する
        foreach (var listKey in new[] { @"Software\HarutamaProject\MediaViewer\Capabilities\FileAssociations", @"Software\Classes\Applications\MediaViewer.exe\SupportedTypes" })
        {
            var names = doc.Descendants(Wix + "RegistryValue").Where(v => (string?)v.Attribute("Key") == listKey).Select(v => (string)v.Attribute("Name")!).ToList();
            CollectionAssert.AreEquivalent(associations.Keys.ToList(), names, listKey);
        }
    }

    [GeneratedRegex(@"^Software\\Classes\\(\.[a-z0-9]+)\\OpenWithProgids$")]
    private static partial Regex OpenWithProgidsKey();

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MediaViewer.slnx"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("リポジトリの場所が見つかりません。");
    }
}
