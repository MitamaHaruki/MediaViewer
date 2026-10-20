using MediaViewer.Core.Updates;

namespace MediaViewer.Tests.Updates;

[TestClass]
public sealed class UpdaterArgumentsTests
{
    [TestMethod]
    public void RoundTrip_WithSpacesAndRollback()
    {
        var args = new UpdaterArguments(@"C:\Program Files\MediaViewer", SemanticVersion.Parse("1.0.0"), SemanticVersion.Parse("1.1.0"),
            @"C:\Users\a b\AppData\Local\MediaViewer\updates\1.1.0\MediaViewer-1.1.0-x64.msi",
            @"C:\Users\a b\AppData\Local\MediaViewer\updates\1.1.0\MediaViewer-1.1.0-x64.msi.sig",
            @"C:\Users\a b\AppData\Local\MediaViewer\updates\1.0.0\MediaViewer-1.0.0-x64.msi",
            @"C:\Users\a b\AppData\Local\MediaViewer\updates\1.0.0\MediaViewer-1.0.0-x64.msi.sig",
            1234, Relaunch: true);

        var parsed = UpdaterArguments.TryParse(args.ToArgumentList(), out var error);

        Assert.AreEqual(args, parsed, error);
    }

    [TestMethod]
    public void RoundTrip_Minimal()
    {
        var args = new UpdaterArguments(@"D:\Apps\MV", SemanticVersion.Parse("1.0.0"), SemanticVersion.Parse("2.0.0"),
            @"D:\u\m.msi", @"D:\u\m.msi.sig", null, null, null, Relaunch: false);
        Assert.AreEqual(args, UpdaterArguments.TryParse(args.ToArgumentList(), out _));
    }

    [TestMethod]
    [DataRow(new string[0])]
    [DataRow(new[] { "install" })]
    [DataRow(new[] { "apply", "--install-dir", @"relative\dir", "--from", "1.0.0", "--to", "1.1.0", "--msi", @"C:\m.msi", "--sig", @"C:\m.sig" })]
    [DataRow(new[] { "apply", "--install-dir", @"C:\d", "--from", "x", "--to", "1.1.0", "--msi", @"C:\m.msi", "--sig", @"C:\m.sig" })]
    [DataRow(new[] { "apply", "--install-dir", @"C:\d", "--from", "1.0.0", "--to", "1.1.0", "--msi", @"C:\m.msi" })]
    [DataRow(new[] { "apply", "--install-dir", @"C:\d", "--from", "1.0.0", "--to", "1.1.0", "--msi", @"C:\m.msi", "--sig", @"C:\m.sig", "--rollback-msi", @"C:\r.msi" })]
    [DataRow(new[] { "apply", "--install-dir", @"C:\d", "--install-dir", @"C:\e", "--from", "1.0.0", "--to", "1.1.0", "--msi", @"C:\m.msi", "--sig", @"C:\m.sig" })]
    [DataRow(new[] { "apply", "--install-dir", @"C:\d", "--from", "1.0.0", "--to", "1.1.0", "--msi", @"C:\m.msi", "--sig", @"C:\m.sig", "--wait-pid", "-5" })]
    public void Invalid_IsRejected(string[] args)
    {
        Assert.IsNull(UpdaterArguments.TryParse(args, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }
}
