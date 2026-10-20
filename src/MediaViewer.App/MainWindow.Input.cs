using MediaViewer.App.Playback;
using MediaViewer.Core.Media;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using PointerDeviceType = Microsoft.UI.Input.PointerDeviceType;

namespace MediaViewer.App;

/// <summary>
/// マウス・タッチ・キーボード・ドラッグ＆ドロップ。画像と動画は同じ操作で拡大縮小・移動する（仕様 §6）。
/// </summary>
public sealed partial class MainWindow
{
    private const double WheelZoomStep = 1.2;     // ホイール1ノッチの倍率
    private const double SwipeMinDistance = 80;   // スワイプと判定する横移動量（DIP）
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);
    private const double VolumeStep = 5;

    private uint? _mousePanPointer;
    private Point _mousePanLast;
    private int _activeTouchPoints;
    private bool _multiTouchInGesture;
    private bool _gestureStartedAtFit;
    private bool _manipulating;

    // ---- マウス ----

    private void OnInputPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        ShowControlsTemporarily();
        if (!IsZoomable) return;
        var point = e.GetCurrentPoint(InputLayer);
        if (point.Properties.IsHorizontalMouseWheel) return;
        var notches = point.Properties.MouseWheelDelta / 120.0;
        _viewport.ZoomAt(point.Position.X, point.Position.Y, Math.Pow(WheelZoomStep, notches));
        e.Handled = true;
    }

    private void OnInputPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // ビューワーを触ったらキー操作の対象をビューワーにする（Space がボタンに奪われないように）
        FocusViewer(FocusState.Pointer);
        Trace($"pressed {e.Pointer.PointerDeviceType} id={e.Pointer.PointerId}");
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _activeTouchPoints++;
            if (_activeTouchPoints > 1) _multiTouchInGesture = true;
            return;
        }
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(InputLayer);
        // 左ドラッグでのパンは拡大中のみ（仕様 §6.2）
        if (point.Properties.IsLeftButtonPressed && IsZoomable && _viewport.CanPan)
        {
            if (InputLayer.CapturePointer(e.Pointer))
            {
                _mousePanPointer = e.Pointer.PointerId;
                _mousePanLast = point.Position;
                e.Handled = true;
            }
        }
    }

    private void OnInputPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            ShowControlsTemporarily();
        }
        if (_mousePanPointer != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(InputLayer).Position;
        _viewport.Pan(position.X - _mousePanLast.X, position.Y - _mousePanLast.Y);
        _mousePanLast = position;
        e.Handled = true;
    }

    private void OnInputPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _activeTouchPoints = Math.Max(0, _activeTouchPoints - 1);
            if (_activeTouchPoints == 0 && !_manipulating) _multiTouchInGesture = false;
            return;
        }
        if (_mousePanPointer == e.Pointer.PointerId)
        {
            InputLayer.ReleasePointerCapture(e.Pointer);
            _mousePanPointer = null;
        }
    }

    // ---- タッチ（ピンチ・パン・スワイプ） ----

    private void OnInputManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        // マウス操作は上のポインターイベントで処理済み。二重適用しない（仕様 §6.2）
        if (e.PointerDeviceType == PointerDeviceType.Mouse)
        {
            e.Complete();
            return;
        }
        Trace($"manip started {e.PointerDeviceType}");
        _manipulating = true;
        _gestureStartedAtFit = !IsZoomable || _viewport.IsAtFit;
    }

    private void OnInputManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        Trace($"manip delta {e.PointerDeviceType} t={e.Delta.Translation} s={e.Delta.Scale:F3}");
        if (e.PointerDeviceType == PointerDeviceType.Mouse || !IsZoomable) return;
        var d = e.Delta;
        // パン → 2本指の中心を基準に拡大縮小。指の下の点が飛ばないよう同じ基準点を使う
        _viewport.Pan(d.Translation.X, d.Translation.Y);
        if (Math.Abs(d.Scale - 1) > 1e-4)
        {
            _viewport.ZoomAt(e.Position.X, e.Position.Y, d.Scale);
        }
        e.Handled = true;
    }

    private void OnInputManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Mouse) return;
        _manipulating = false;
        var c = e.Cumulative;
        Trace($"manip completed t={c.Translation} s={c.Scale:F3} multi={_multiTouchInGesture} fitStart={_gestureStartedAtFit}");
        // フィット表示中の1本指の横スワイプだけをファイル移動とみなす（拡大中はパンを優先、仕様 §6.2）
        var isSwipe = _currentPath is not null
            && _gestureStartedAtFit
            && !_multiTouchInGesture
            && (!IsZoomable || _viewport.IsAtFit)
            && Math.Abs(c.Scale - 1) < 0.05
            && Math.Abs(c.Translation.X) > SwipeMinDistance
            && Math.Abs(c.Translation.X) > 2 * Math.Abs(c.Translation.Y);
        if (_activeTouchPoints == 0) _multiTouchInGesture = false;
        if (isSwipe)
        {
            Navigate(forward: c.Translation.X < 0);
        }
    }

    private void OnInputDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // マルチタッチ中の誤ったダブルタップは無視する（仕様 §6.2）
        if (_multiTouchInGesture || !IsZoomable) return;
        var p = e.GetPosition(InputLayer);
        _viewport.ToggleZoom(p.X, p.Y);
        e.Handled = true;
    }

    private void OnInputTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Touch) ShowControlsTemporarily();
    }

    // ---- キーボード（仕様 §14） ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox) return;
        var inMenu = focused is MenuBarItem or MenuFlyoutItemBase;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

#if DEBUG
        // 開発時の確認用：Ctrl+Shift+Alt+D で GPU デバイス消失を模擬する
        if (e.Key == VirtualKey.D && ctrl
            && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)
            && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
        {
            _renderer?.SimulateDeviceLost();
            e.Handled = true;
            return;
        }
#endif

        switch (e.Key)
        {
            case VirtualKey.O when ctrl:
                _ = PickAndOpenAsync();
                break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 when ctrl:
                _viewport.ResetToFit();
                break;
            case VirtualKey.F11:
                SetFullScreen(!_isFullScreen);
                break;
            case VirtualKey.Escape when _isFullScreen:
                SetFullScreen(false);
                break;
            case VirtualKey.Space when IsPlayable:
                _playback!.TogglePlayPause();
                break;
            case VirtualKey.Left or VirtualKey.Right when !inMenu:
                var forward = e.Key == VirtualKey.Right;
                if (IsPlayable) _playback!.SeekBy(forward ? SeekStep : -SeekStep);
                else Navigate(forward);
                UpdatePosition();
                break;
            case VirtualKey.Up or VirtualKey.Down when !inMenu:
                VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Key == VirtualKey.Up ? VolumeStep : -VolumeStep), 0, 100);
                break;
            case VirtualKey.M:
                SetMuted(!_isMuted);
                break;
            case VirtualKey.PageUp:
                Navigate(forward: false);
                break;
            case VirtualKey.PageDown:
                Navigate(forward: true);
                break;
            default:
                return;
        }
        e.Handled = true;
        ShowControlsTemporarily();
    }

    // ---- ドラッグ＆ドロップ（仕様 §3.2） ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }
        // 複数ドロップ時は最初の対応ファイルを開く
        var files = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var path = files.FirstOrDefault(MediaKinds.IsSupported) ?? files.FirstOrDefault();
        if (path is not null) await OpenAsync(path);
    }

    private static readonly bool s_traceInput = Environment.GetEnvironmentVariable("MEDIAVIEWER_TRACE_INPUT") == "1";

    private static void Trace(string message)
    {
        if (s_traceInput) MediaViewer.Core.Diagnostics.AppLog.Info("[input] " + message);
    }
}