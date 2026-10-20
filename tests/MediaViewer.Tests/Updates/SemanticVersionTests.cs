using MediaViewer.Core.Updates;

namespace MediaViewer.Tests.Updates;

[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    [DataRow("1.2.3", 1, 2, 3, null)]
    [DataRow("v1.2.3", 1, 2, 3, null)]
    [DataRow("1.2", 1, 2, 0, null)]
    [DataRow("1.0.0-beta.2", 1, 0, 0, "beta.2")]
    [DataRow("0.1.0+abc123", 0, 1, 0, null)]
    [DataRow("10.20.30-rc.1+build.5", 10, 20, 30, "rc.1")]
    public void Parse_Valid(string text, int major, int minor, int patch, string? pre)
    {
        Assert.IsTrue(SemanticVersion.TryParse(text, out var v));
        Assert.AreEqual(new SemanticVersion(major, minor, patch, pre), v);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1")]
    [DataRow("1.2.3.4")]
    [DataRow("1.x.0")]
    [DataRow("-1.0.0")]
    [DataRow("1.0.0-")]
    [DataRow("v")]
    [DataRow("1. 2.3")]
    public void Parse_Invalid(string? text)
    {
        Assert.IsFalse(SemanticVersion.TryParse(text, out _));
    }

    [TestMethod]
    public void Compare_IsNumeric_NotLexical()
    {
        // 文字列比較では "1.10.0" < "1.9.0" になってしまう（仕様 §10.2-4）
        Assert.IsTrue(SemanticVersion.Parse("1.10.0") > SemanticVersion.Parse("1.9.0"));
        Assert.IsTrue(SemanticVersion.Parse("2.0.0") > SemanticVersion.Parse("1.99.99"));
        Assert.IsTrue(SemanticVersion.Parse("1.0.10") > SemanticVersion.Parse("1.0.9"));
        Assert.AreEqual(0, SemanticVersion.Parse("v1.2.3").CompareTo(SemanticVersion.Parse("1.2.3")));
    }

    [TestMethod]
    public void Compare_PreReleaseOrdering()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.IsTrue(SemanticVersion.Parse(ordered[i - 1]) < SemanticVersion.Parse(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
        }
    }

    [TestMethod]
    public void ToString_RoundTrips()
    {
        Assert.AreEqual("1.2.3", SemanticVersion.Parse("v1.2.3").ToString());
        Assert.AreEqual("1.2.0", SemanticVersion.Parse("1.2").ToString());
        Assert.AreEqual("1.2.3-rc.1", SemanticVersion.Parse("1.2.3-rc.1+x").ToString());
    }

    [TestMethod]
    public void Schedule_ChecksAfter24Hours()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(UpdateSchedule.IsCheckDue(null, now));
        Assert.IsFalse(UpdateSchedule.IsCheckDue(now.AddHours(-23), now));
        Assert.IsTrue(UpdateSchedule.IsCheckDue(now.AddHours(-24), now));
        // 時計が戻された場合（最終確認が未来）は確認する
        Assert.IsTrue(UpdateSchedule.IsCheckDue(now.AddDays(3), now));
    }
}
