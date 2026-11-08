using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using MediaViewer.App.Sidebar;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MediaViewer.App;

/// <summary>
/// 左のサイドバー（仕様決定事項 D17）：表示中のファイルがあるフォルダの中を、サムネイル付きで一覧表示する。
/// 一覧は前後移動の対象（すべて・画像のみ・動画のみ・音声のみ）に合わせて絞り込み、名前順（自然順）に並べる。
/// 初回は閉じた状態で、以後は前回の開閉状態を使う。フルスクリーン中は隠す。
/// サムネイルは画面に出た項目だけを作業スレッドで作り、メモリ内に覚えておく（全件をデコードしない、仕様 §11.1）。
/// </summary>
public sealed partial class MainWindow
{
    private const double ThumbnailDisplaySize = 72;
    private const int ThumbnailCacheLimit = 600;

    private bool _sidebarOpen;
    private ObservableCollection<SidebarItem> _sidebarItems = [];
    private string? _sidebarFolder;
    private NavigationFilter _sidebarFilter;
    private int _sidebarGeneration;
    private bool _syncingSidebarSelection;
    private readonly SemaphoreSlim _thumbnailGate = new(3);
    private readonly Dictionary<string, (DateTime Stamp, ImageSource Image)> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailOrder = new();

    private void InitializeSidebar(bool open)
    {
        SidebarList.ItemsSource = _sidebarItems;
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
            // 前に作ったサムネイルは、ファイルが変わっていなければ使い回す
            if (_thumbnailCache.TryGetValue(file, out var cached) && cached.Stamp == stamp) item.Thumbnail = cached.Image;
            items.Add(item);
        }
        _sidebarItems = items;
        _syncingSidebarSelection = true;
        SidebarList.ItemsSource = items;
        _syncingSidebarSelection = false;
        var folderName = folder is null ? "" : Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (folder is not null && folderName.Length == 0) folderName = folder;
        SidebarHeader.Text = folder is null ? "" : $"{folderName}（{files.Count} 件）";
        SyncSidebarSelection();
    }

    private void SyncSidebarSelection()
    {
        var current = _sidebarItems.FirstOrDefault(i => string.Equals(i.FilePath, _currentPath, StringComparison.OrdinalIgnoreCase));
        _syncingSidebarSelection = true;
        SidebarList.SelectedItem = current;
        _syncingSidebarSelection = false;
        if (current is not null) SidebarList.ScrollIntoView(current);
    }

    /// <summary>クリックやキー操作で選んだファイルを開く。</summary>
    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebarSelection || SidebarList.SelectedItem is not SidebarItem item) return;
        if (string.Equals(item.FilePath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _ = OpenAsync(item.FilePath);
    }

    /// <summary>項目が画面に出たときに、まだなければサムネイルを読み込む。</summary>
    private void OnSidebarContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SidebarItem item || item.Thumbnail is not null || item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        _ = LoadThumbnailAsync(item, _sidebarGeneration);
    }

    private async Task LoadThumbnailAsync(SidebarItem item, int generation)
    {
        var size = (int)Math.Ceiling(ThumbnailDisplaySize * (Content.XamlRoot?.RasterizationScale ?? 1.0));
        await _thumbnailGate.WaitAsync();
        try
        {
            // 一覧が作り直されていたら（フォルダの移動など）、もう見えないので作らない
            if (generation != _sidebarGeneration)
            {
                item.ThumbnailRequested = false;
                return;
            }
            var (stamp, bitmap) = await Task.Run(async () => (LastWriteTime(item.FilePath), await ThumbnailLoader.LoadAsync(item.FilePath, size, CancellationToken.None)));
            if (bitmap is null) return;
            var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
            bitmap.Pixels.CopyTo(image.PixelBuffer);
            image.Invalidate();
            item.Thumbnail = image;
            RememberThumbnail(item.FilePath, stamp, image);
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

    /// <summary>作ったサムネイルを覚えておく。古いものから捨て、上限を超えないようにする。</summary>
    private void RememberThumbnail(string path, DateTime stamp, ImageSource image)
    {
        if (_thumbnailCache.ContainsKey(path)) _thumbnailOrder.Remove(path);
        _thumbnailCache[path] = (stamp, image);
        _thumbnailOrder.AddLast(path);
        while (_thumbnailOrder.Count > ThumbnailCacheLimit)
        {
            _thumbnailCache.Remove(_thumbnailOrder.First!.Value);
            _thumbnailOrder.RemoveFirst();
        }
    }
}
