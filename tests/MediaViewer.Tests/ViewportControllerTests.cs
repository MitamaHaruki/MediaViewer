using MediaViewer.Core.Viewport;

namespace MediaViewer.Tests;

[TestClass]
public sealed class ViewportControllerTests
{
    private const double Eps = 1e-9;

    private static ViewportController Create(double vw, double vh, double cw, double ch, bool limit, double ppd = 1.0)
    {
        var v = new ViewportController();
        v.SetViewport(vw, vh, ppd);
        v.SetContent(cw, ch, limit);
        return v;
    }

    [TestMethod]
    public void 大きい画像はアスペクト比を保って中央にフィットする()
    {
        var v = Create(1000, 500, 4000, 1000, limit: true);
        Assert.AreEqual(0.25, v.Scale, Eps);
        Assert.AreEqual(0, v.OffsetX, Eps);
        Assert.AreEqual((500 - 250) / 2.0, v.OffsetY, Eps);
        Assert.IsTrue(v.IsAtFit);
        Assert.IsFalse(v.CanPan);
    }

    [TestMethod]
    public void 小さい画像は等倍を上限に表示する_動画は拡大してフィットする()
    {
        var image = Create(1000, 1000, 200, 100, limit: true);
        Assert.AreEqual(1.0, image.Scale, Eps);

        var video = Create(1000, 1000, 200, 100, limit: false);
        Assert.AreEqual(5.0, video.Scale, Eps);
    }

    [TestMethod]
    public void 等倍はDPIスケールを考慮する()
    {
        // 150% DPI：素材1px = 物理1px = 1/1.5 DIP
        var v = Create(1000, 1000, 300, 300, limit: true, ppd: 1.5);
        Assert.AreEqual(1 / 1.5, v.Scale, Eps);
        Assert.AreEqual(1 / 1.5, v.ActualSizeScale, Eps);
    }

    [TestMethod]
    public void ZoomAtは指定点の下のコンテンツ点を動かさない()
    {
        var v = Create(1000, 1000, 2000, 2000, limit: true);
        var (ax, ay) = (300.0, 700.0);
        var cx = (ax - v.OffsetX) / v.Scale;
        var cy = (ay - v.OffsetY) / v.Scale;

        v.ZoomAt(ax, ay, 3.0);

        Assert.AreEqual(1.5, v.Scale, Eps);
        Assert.AreEqual(ax, cx * v.Scale + v.OffsetX, 1e-6);
        Assert.AreEqual(ay, cy * v.Scale + v.OffsetY, 1e-6);
    }

    [TestMethod]
    public void 倍率はフィットから等倍の32倍の範囲に制限される()
    {
        var v = Create(1000, 1000, 2000, 2000, limit: true);
        v.ZoomAt(500, 500, 0.01);
        Assert.AreEqual(v.FitScale, v.Scale, Eps);

        v.ZoomAt(500, 500, 1e6);
        Assert.AreEqual(32.0, v.Scale, Eps);
    }

    [TestMethod]
    public void パンは端に余白ができない範囲に制限される()
    {
        var v = Create(1000, 1000, 2000, 2000, limit: true);
        v.ZoomTo(1.0, 500, 500); // 2000x2000 を表示、範囲は [-1000, 0]
        v.Pan(5000, -5000);
        Assert.AreEqual(0, v.OffsetX, Eps);
        Assert.AreEqual(-1000, v.OffsetY, Eps);
    }

    [TestMethod]
    public void フィット中はパンしない()
    {
        var v = Create(1000, 1000, 2000, 1000, limit: true);
        var (ox, oy) = (v.OffsetX, v.OffsetY);
        v.Pan(100, 100);
        Assert.AreEqual(ox, v.OffsetX, Eps);
        Assert.AreEqual(oy, v.OffsetY, Eps);
    }

    [TestMethod]
    public void ダブルクリックはフィットと等倍を切り替える()
    {
        var v = Create(1000, 1000, 4000, 4000, limit: true);
        v.ToggleZoom(500, 500);
        Assert.AreEqual(1.0, v.Scale, Eps);
        Assert.IsFalse(v.IsAtFit);

        v.ToggleZoom(500, 500);
        Assert.IsTrue(v.IsAtFit);
        Assert.AreEqual(0.25, v.Scale, Eps);
    }

    [TestMethod]
    public void 等倍がフィット以下ならダブルクリックはフィットの2倍にする()
    {
        var v = Create(1000, 1000, 200, 200, limit: true);
        Assert.AreEqual(1.0, v.Scale, Eps);
        v.ToggleZoom(500, 500);
        Assert.AreEqual(2.0, v.Scale, Eps);
    }

    [TestMethod]
    public void フィット中にウィンドウサイズが変わるとフィットを維持する()
    {
        var v = Create(1000, 1000, 2000, 2000, limit: true);
        v.SetViewport(500, 400, 1.0);
        Assert.IsTrue(v.IsAtFit);
        Assert.AreEqual(0.2, v.Scale, Eps);
    }

    [TestMethod]
    public void 拡大中にウィンドウサイズが変わると中心のコンテンツ点を保つ()
    {
        var v = Create(1000, 1000, 4000, 4000, limit: true);
        v.ZoomTo(1.0, 250, 250);
        var cx = (500 - v.OffsetX) / v.Scale;
        var cy = (500 - v.OffsetY) / v.Scale;

        v.SetViewport(800, 600, 1.0);

        Assert.AreEqual(1.0, v.Scale, Eps);
        Assert.AreEqual(cx, (400 - v.OffsetX) / v.Scale, 1e-6);
        Assert.AreEqual(cy, (300 - v.OffsetY) / v.Scale, 1e-6);
    }

    [TestMethod]
    public void 新しいコンテンツはフィットに戻る()
    {
        var v = Create(1000, 1000, 4000, 4000, limit: true);
        v.ZoomTo(2.0, 500, 500);
        v.SetContent(1920, 1080, limitFitToActualSize: false);
        Assert.IsTrue(v.IsAtFit);
    }
}
