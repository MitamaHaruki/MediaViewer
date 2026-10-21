using MediaViewer.Core.Navigation;
using MediaViewer.Core.Settings;

namespace MediaViewer.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private string _dir = null!;
    private string _path = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MediaViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [TestMethod]
    public void ファイルがなければ仕様どおりの初期値になる()
    {
        var s = new SettingsStore(_path).Load();
        Assert.AreEqual(NavigationFilter.All, s.NavigationFilter);
        Assert.AreEqual(AppSettings.DefaultVolume, s.Volume);
        Assert.IsFalse(s.IsMuted);
        Assert.IsFalse(s.RepeatEnabled, "リピートは初期値 OFF（終端で停止）");
        Assert.AreEqual(5, s.SeekStepSeconds);
        Assert.IsNull(s.Window);
        Assert.IsTrue(s.Update.AutoCheck);
        Assert.IsTrue(s.Update.AutoDownload);
        Assert.AreEqual(UpdateApplyMode.OnExit, s.Update.ApplyMode);
        Assert.IsNull(s.Update.LastCheckedUtc);
    }

    [TestMethod]
    public void 保存した内容を読み戻せる()
    {
        var store = new SettingsStore(_path);
        var checkedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(store.Update(s =>
        {
            s.NavigationFilter = NavigationFilter.Videos;
            s.Volume = 0.35;
            s.IsMuted = true;
            s.RepeatEnabled = true;
            s.SeekStepSeconds = 10;
            s.Window = new WindowPlacement { X = 100, Y = -50, Width = 1200, Height = 800, IsMaximized = true };
            s.Update.LastCheckedUtc = checkedAt;
        }));

        var r = new SettingsStore(_path).Load();
        Assert.AreEqual(NavigationFilter.Videos, r.NavigationFilter);
        Assert.AreEqual(0.35, r.Volume, 1e-9);
        Assert.IsTrue(r.IsMuted);
        Assert.IsTrue(r.RepeatEnabled);
        Assert.AreEqual(10, r.SeekStepSeconds);
        Assert.AreEqual(-50, r.Window!.Y);
        Assert.IsTrue(r.Window.IsMaximized);
        Assert.AreEqual(checkedAt, r.Update.LastCheckedUtc);
        Assert.IsFalse(Directory.GetFiles(_dir, "*.tmp").Any(), "一時ファイルが残らない");
    }

    [TestMethod]
    public void 壊れたファイルは退避して既定値で復旧する()
    {
        File.WriteAllText(_path, "{ \"volume\": 0.5, 壊れた");
        var s = new SettingsStore(_path).Load();

        Assert.AreEqual(AppSettings.DefaultVolume, s.Volume);
        Assert.IsFalse(File.Exists(_path));
        Assert.AreEqual(1, Directory.GetFiles(_dir, "settings.json.broken-*").Length);
    }

    [TestMethod]
    public void 範囲外の値は安全な値に直す()
    {
        File.WriteAllText(_path, """
            { "volume": 7, "seekStepSeconds": 3, "navigationFilter": "Unknown", "window": { "x": 0, "y": 0, "width": 10, "height": 10 } }
            """);
        var s = new SettingsStore(_path).Load();

        Assert.AreEqual(1.0, s.Volume);
        Assert.AreEqual(5, s.SeekStepSeconds);
        Assert.IsNull(s.Window);
    }

    [TestMethod]
    public void 別プロセスの変更を上書きで消さない()
    {
        // 2つのウィンドウ（プロセス）がそれぞれ別の項目を変更する
        var a = new SettingsStore(_path);
        var b = new SettingsStore(_path);
        a.Load();
        b.Update(s => s.Volume = 0.2);
        a.Update(s => s.NavigationFilter = NavigationFilter.Audio);

        var r = new SettingsStore(_path).Load();
        Assert.AreEqual(0.2, r.Volume, 1e-9);
        Assert.AreEqual(NavigationFilter.Audio, r.NavigationFilter);
    }

    [TestMethod]
    public void 並行して保存しても壊れない()
    {
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            new SettingsStore(_path).Update(s => s.Volume = i / 20.0))).ToArray();
        Task.WaitAll(tasks);

        Assert.IsTrue(tasks.All(t => t.Result));
        var r = new SettingsStore(_path).Load();
        Assert.IsTrue(r.Volume is >= 0 and <= 0.75);
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.broken-*").Length);
    }

    [TestMethod]
    public void 新しい版が書いた知らない項目を保存で消さない()
    {
        File.WriteAllText(_path, """
            { "schemaVersion": 2, "volume": 0.5, "futureOption": { "enabled": true }, "update": { "autoCheck": false, "channel": "beta" } }
            """);
        new SettingsStore(_path).Update(s => s.IsMuted = true);

        var json = File.ReadAllText(_path);
        StringAssert.Contains(json, "futureOption");
        StringAssert.Contains(json, "\"channel\": \"beta\"");
        StringAssert.Contains(json, "\"schemaVersion\": 2");
        var r = new SettingsStore(_path).Load();
        Assert.IsTrue(r.IsMuted);
        Assert.IsFalse(r.Update.AutoCheck);
    }
}
