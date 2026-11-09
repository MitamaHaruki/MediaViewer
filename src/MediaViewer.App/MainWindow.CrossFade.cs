using System.Diagnostics;
using MediaViewer.Core.Media;
using Microsoft.UI.Xaml.Media;

namespace MediaViewer.App;

/// <summary>
/// スライドショーで画像を切り替えるときのクロスフェード（仕様決定事項 D18）。
/// 切り替える前に今の画面を写し取り、次の画像を表示し終えたら、写しを約0.8秒かけて薄くしていく。
/// 読み込み中は写しをそのまま見せるので、切り替えの途中で画面が空になることもない。
/// スライドショーの実行中は、自動の切り替えも、手での前後移動・サイドバーからの選択もすべてフェードにする。
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan CrossFadeDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>写しを取り、次の画像の表示を待っている。</summary>
    private bool _crossFadeArmed;

    /// <summary>フェードの描画を毎フレーム行っている。</summary>
    private bool _crossFadeRunning;
    private long _crossFadeStart;

    private bool ShouldCrossFade(string target) =>
        _autoPlay && AvailableAutoPlay == AutoPlayMode.Slideshow
        && _currentPath is not null
        && MediaKinds.FromPath(target) is MediaKind.Image or MediaKind.Unknown;

    /// <summary>ファイルを開く直前に呼ぶ。フェードするなら今の画面を写し取り、しないなら前のフェードを終わらせる。</summary>
    private void PrepareCrossFade(string target)
    {
        StopCrossFadeAnimation();
        _crossFadeArmed = false;
        if (_renderer is null) return;
        if (ShouldCrossFade(target) && _renderer.BeginCrossFade())
        {
            _crossFadeArmed = true;
            return;
        }
        _renderer.EndCrossFade();
    }

    /// <summary>ファイルを開き終えたときに呼ぶ。写しを取ってあれば、薄くしていく描画を始める。</summary>
    private void StartCrossFade()
    {
        if (!_crossFadeArmed || _renderer is null) return;
        _crossFadeArmed = false;
        _crossFadeStart = Stopwatch.GetTimestamp();
        if (!_crossFadeRunning)
        {
            CompositionTarget.Rendering += OnCrossFadeFrame;
            _crossFadeRunning = true;
        }
    }

    /// <summary>画面の更新ごとに、経過時間に合わせて前の画面の不透明度を下げる（始めと終わりをなめらかにする）。</summary>
    private void OnCrossFadeFrame(object? sender, object e)
    {
        if (_renderer is not { } renderer)
        {
            StopCrossFadeAnimation();
            return;
        }
        var t = Stopwatch.GetElapsedTime(_crossFadeStart) / CrossFadeDuration;
        if (t >= 1)
        {
            StopCrossFadeAnimation();
            renderer.EndCrossFade();
            renderer.Render();
            // 写しに使った GPU メモリをすぐ返す（仕様 §11.1）
            renderer.TrimMemory();
            return;
        }
        var eased = t * t * (3 - 2 * t);
        renderer.SetCrossFade((float)(1 - eased));
        renderer.Render();
    }

    private void StopCrossFadeAnimation()
    {
        if (!_crossFadeRunning) return;
        CompositionTarget.Rendering -= OnCrossFadeFrame;
        _crossFadeRunning = false;
    }
}
