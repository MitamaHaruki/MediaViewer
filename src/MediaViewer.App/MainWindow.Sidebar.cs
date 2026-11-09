using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using MediaViewer.App.Sidebar;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Navigation;
using MediaViewer.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MediaViewer.App;

/// <summary>
/// 左のサイドバー（仕様決定事項 D17・D19）：表示中のファイルがあるフォルダの中を、サムネイル付きで一覧表示する。
/// 一覧は前後移動の対象（すべて・画像のみ・動画のみ・音声のみ）に合わせて絞り込み、名前順（自然順）に並べる。
/// 表示形式は「一覧（サムネイルとファイル名）」と「サムネイルだけを横2列」を切り替えられ、設定に保存する。
/// 初回は閉じた状態で、以後は前回の開閉状態を使う。フルスクリーン中は隠す。
/// 項目を外（エクスプローラー・デスクトップ・他のアプリ）へドラッグすると、そのファイルをコピーできる（移動はしない）。
/// サムネイルは画面に出た項目だけを作業スレッドで作り、メモリ内に覚えておく（全件をデコードしない、仕様 §11.1）。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>一覧表示のサムネイルの大きさ（画面上の大きさ。実際のピクセル数は表示倍率を掛ける）。</summary>
    private const double ListThumbnailSize = 72;

    /// <summary>2列表示のサムネイルの大きさ。</summary>
    private const double GridThumbnailSize = 116;

    /// <summary>覚えておくサムネイルの上限（件数と、画素データの合計バイト数）。</summary>
    private const int ThumbnailCacheLimit = 600;
    private const long ThumbnailCacheBytesLimit = 48L * 1024 * 1024;

    private sealed record ThumbnailEntry(DateTime Stamp, int PixelSize, ImageSource Image, long Bytes);

    private bool _sidebarOpen;
    private SidebarLayout _sidebarLayout;
    private ObservableCollection<SidebarItem> _sidebarItems = [];
    private string? _sidebarFolder;
    private NavigationFilter _sidebarFilter;
    private int _sidebarGeneration;
    private bool _syncingSidebarSelection;
    private readonly SemaphoreSlim _thumbnailGate = new(3);
    private readonly Dictionary<string, ThumbnailEntry> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailOrder = new();
    private long _thumbnailCacheBytes;

    private void InitializeSidebar(bool open, SidebarLayout layout)
    {
        SidebarList.ItemsSource = _sidebarItems;
        SidebarGrid.ItemsSource = _sidebarItems;
        // 一覧の項目を押した時点で、ドラッグに使うファイルの情報を用意し始める（一覧が押下を処理済みにしても受け取る）
        SidebarList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSidebarPointerPressed), handledEventsToo: true);
        SidebarGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSidebarPointerPressed), handledEventsToo: true);
        SetSidebarLayout(layout, save: false);
        SetSidebarOpen(open, save: false);
    }

    private void OnSidebarClick(object sender, RoutedEventArgs e) => SetSidebarOpen(SidebarButton.IsChecked == true, save: true);

    private void OnSidebarMenuClick(object sender, RoutedEventArgs e) => SetSidebarOpen(MenuSidebar.IsChecked, save: true);

    private void SetSidebarOpen(bool open, bool save)
    {
        var changed = _sidebarOpen != open;
        _sidebarOpen = open;
        SidebarButton.IsChecked = MenuSidebar.IsChecked = open;
        UpdateSidebarVisibility();
        if (open && changed) RefreshSidebar(force: true);
        if (save && changed) SaveSettings(s => s.SidebarOpen = open);
    }

    /// <summary>フルスクリーン中はサイドバーを隠す（開閉状態はそのまま）。</summary>
    private void UpdateSidebarVisibility()
    {
        Sidebar.Visibility = _sidebarOpen && !_isFullScreen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 表示形式（一覧／サムネイル2列） ----

    private void OnSidebarListModeClick(object sender, RoutedEventArgs e) => SetSidebarLayout(SidebarLayout.List, save: true);

    private void OnSidebarGridModeClick(object sender, RoutedEventArgs e) => SetSidebarLayout(SidebarLayout.Grid, save: true);

    private ListViewBase ActiveSidebarList => _sidebarLayout == SidebarLayout.Grid ? SidebarGrid : SidebarList;

    private void SetSidebarLayout(SidebarLayout layout, bool save)
    {
        var changed = _sidebarLayout != layout;
        _sidebarLayout = layout;
        var grid = layout == SidebarLayout.Grid;
        // 押したボタンが選ばれていない見た目に戻らないよう、常に今の形式だけを選択状態にする
        SidebarListModeButton.IsChecked = !grid;
        SidebarGridModeButton.IsChecked = grid;
        SidebarList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        SidebarGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        if (!changed) return;
        // 表示された側の一覧で、表示中のファイルが見える位置まで動かす（並べ終えてから）
        DispatcherQueue.TryEnqueue(SyncSidebarSelection);
        if (save) SaveSettings(s => s.SidebarLayout = layout);
    }

    /// <summary>今の表示形式で使うサムネイルの大きさ（長辺のピクセル数）。</summary>
    private int SidebarThumbnailPixelSize =>
        (int)Math.Ceiling((_sidebarLayout == SidebarLayout.Grid ? GridThumbnailSize : ListThumbnailSize)
            * (Content.XamlRoot?.RasterizationScale ?? 1.0));

    // ---- 一覧の内容 ----

    /// <summary>
    /// 一覧を表示中のファイルに合わせる。フォルダか前後移動の対象が変わったとき（force のときも）はフォルダを列挙し直し、
    /// 同じフォルダなら選択だけを動かす。
    /// </summary>
    private async void RefreshSidebar(bool force = false)
    {
        if (!_sidebarOpen) return;
        var path = _currentPath;
        var folder = path is null ? null : Path.GetDirectoryName(path);
        var filter = _filter;
        if (folder is null)
        {
            ReplaceSidebarItems([], null, filter);
            return;
        }
        if (!force && folder == _sidebarFolder && filter == _sidebarFilter && _sidebarItems.Any(i => i.FilePath == path))
        {
            SyncSidebarSelection();
            return;
        }
        var generation = ++_sidebarGeneration;
        var files = await Task.Run(() => FolderNavigator.List(folder, filter).Select(f => (f, LastWriteTime(f))).ToList());
        if (generation != _sidebarGeneration || !_sidebarOpen) return;
        ReplaceSidebarItems(files, folder, filter);
    }

    private static DateTime LastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private void ReplaceSidebarItems(IReadOnlyList<(string Path, DateTime Stamp)> files, string? folder, NavigationFilter filter)
    {
        _sidebarGeneration++;
        _sidebarFolder = folder;
        _sidebarFilter = filter;
        var items = new ObservableCollection<SidebarItem>();
        foreach (var (file, stamp) in files)
        {
            var item = new SidebarItem(file);
            // 前に作ったサムネイルは、ファイルが変わっていなければ使い回す（小さければ、画面に出たときに作り直す）
            if (_thumbnailCache.TryGetValue(file, out var cached) && cached.Stamp == stamp)
            {
                item.Thumbnail = cached.Image;
                item.ThumbnailPixelSize = cached.PixelSize;
            }
            items.Add(item);
        }
        _sidebarItems = items;
        _syncingSidebarSelection = true;
        SidebarList.ItemsSource = items;
        SidebarGrid.ItemsSource = items;
        _syncingSidebarSelection = false;
        var folderName = folder is null ? "" : Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (folder is not null && folderName.Length == 0) folderName = folder;
        SidebarHeader.Text = folder is null ? "" : $"{folderName}（{files.Count} 件）";
        ToolTipService.SetToolTip(SidebarHeader, folder);
        SyncSidebarSelection();
    }

    private void SyncSidebarSelection()
    {
        var current = _sidebarItems.FirstOrDefault(i => string.Equals(i.FilePath, _currentPath, StringComparison.OrdinalIgnoreCase));
        _syncingSidebarSelection = true;
        SidebarList.SelectedItem = current;
        SidebarGrid.SelectedItem = current;
        _syncingSidebarSelection = false;
        if (current is not null) ActiveSidebarList.ScrollIntoView(current);
    }

    /// <summary>クリックやキー操作で選んだファイルを開く。</summary>
    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebarSelection || sender is not ListViewBase list || list.Visibility != Visibility.Visible) return;
        if (list.SelectedItem is not SidebarItem item) return;
        if (string.Equals(item.FilePath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _ = OpenAsync(item.FilePath);
    }

    // ---- 外へのドラッグ（ファイルのコピー） ----

    private void OnSidebarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Trace($"sidebar pressed {e.Pointer.PointerDeviceType} src={e.OriginalSource?.GetType().Name}");
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarItem item) _ = item.GetStorageFileAsync();
    }

    /// <summary>
    /// 項目をドラッグし始めたとき：ファイルとして外へ渡す。受け取る側（エクスプローラー等）には「コピー」だけを許し、
    /// 元のファイルが移動・削除されることはない。
    /// </summary>
    private void OnSidebarDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<SidebarItem>().Where(i => File.Exists(i.FilePath)).ToList();
        Trace($"sidebar drag starting items={items.Count}");
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.Title = items[0].Name;
        var tasks = items.Select(i => i.GetStorageFileAsync()).ToList();
        if (tasks.All(t => t.IsCompletedSuccessfully))
        {
            e.Data.SetStorageItems(tasks.Select(t => (IStorageItem)t.Result).ToList(), readOnly: true);
            return;
        }
        // まだ用意できていなければ、受け取る側が求めたときに渡す
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = await Task.WhenAll(tasks);
                request.SetData(files.Cast<IStorageItem>().ToList());
            }
            catch (Exception ex)
            {
                AppLog.Warn("ドラッグするファイルを用意できませんでした。", ex);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnSidebarDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        Trace($"sidebar drag completed result={args.DropResult}");

    // ---- サムネイル ----

    /// <summary>項目が画面に出たときに、今の表示形式に足りる大きさのサムネイルがなければ読み込む。</summary>
    private void OnSidebarContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SidebarItem item) return;
        var size = SidebarThumbnailPixelSize;
        if (item.ThumbnailPixelSize >= size || item.RequestedPixelSize >= size) return;
        item.RequestedPixelSize = size;
        _ = LoadThumbnailAsync(item, size, _sidebarGeneration);
    }

    private async Task LoadThumbnailAsync(SidebarItem item, int size, int generation)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            // 一覧が作り直されていたら（フォルダの移動など）、もう見えないので作らない
            if (generation != _sidebarGeneration)
            {
                item.RequestedPixelSize = 0;
                return;
            }
            var (stamp, bitmap) = await Task.Run(async () => (LastWriteTime(item.FilePath), await ThumbnailLoader.LoadAsync(item.FilePath, size, CancellationToken.None)));
            if (bitmap is null) return;
            var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
            bitmap.Pixels.CopyTo(image.PixelBuffer);
            image.Invalidate();
            item.Thumbnail = image;
            item.ThumbnailPixelSize = size;
            RememberThumbnail(item.FilePath, new ThumbnailEntry(stamp, size, image, bitmap.Pixels.LongLength));
        }
        catch (Exception e)
        {
            AppLog.Warn("サムネイルを作れませんでした。", e);
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    /// <summary>作ったサムネイルを覚えておく。古いものから捨て、件数とメモリの上限を超えないようにする。</summary>
    private void RememberThumbnail(string path, ThumbnailEntry entry)
    {
        if (_thumbnailCache.Remove(path, out var old))
        {
            _thumbnailOrder.Remove(path);
            _thumbnailCacheBytes -= old.Bytes;
        }
        _thumbnailCache[path] = entry;
        _thumbnailOrder.AddLast(path);
        _thumbnailCacheBytes += entry.Bytes;
        while (_thumbnailOrder.Count > 1
            && (_thumbnailOrder.Count > ThumbnailCacheLimit || _thumbnailCacheBytes > ThumbnailCacheBytesLimit))
        {
            var oldest = _thumbnailOrder.First!.Value;
            _thumbnailOrder.RemoveFirst();
            if (_thumbnailCache.Remove(oldest, out var removed)) _thumbnailCacheBytes -= removed.Bytes;
        }
    }
}
