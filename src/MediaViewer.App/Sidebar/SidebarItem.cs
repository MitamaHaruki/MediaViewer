using System.ComponentModel;
using MediaViewer.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace MediaViewer.App.Sidebar;

/// <summary>サイドバーの1項目（フォルダ内の1ファイル）。サムネイルは画面に出たときに読み込む。</summary>
public sealed partial class SidebarItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public SidebarItem(string path)
    {
        FilePath = path;
        Name = Path.GetFileName(path);
        Kind = MediaKinds.FromPath(path);
    }

    public string FilePath { get; }
    public string Name { get; }
    public MediaKind Kind { get; }

    /// <summary>サムネイルがないときの印（画像・動画・音声）。</summary>
    public string KindGlyph => Kind switch
    {
        MediaKind.Video => "",
        MediaKind.Audio => "",
        _ => "",
    };

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
        }
    }

    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>今のサムネイルを作ったときの大きさ（長辺のピクセル数。なければ 0）。</summary>
    internal int ThumbnailPixelSize { get; set; }

    /// <summary>読み込み中（または読み込めなかった）サムネイルの大きさ。同じ大きさで何度も読み込まないための印。</summary>
    internal int RequestedPixelSize { get; set; }

    private Task<StorageFile>? _storageFile;

    /// <summary>
    /// ドラッグで外へ渡すファイルの情報を先に用意する。ドラッグの開始（指やマウスが少し動いたとき）までに
    /// 用意できていれば、そのまま渡せる。
    /// </summary>
    internal Task<StorageFile> GetStorageFileAsync()
    {
        if (_storageFile is null || _storageFile.IsFaulted || _storageFile.IsCanceled)
        {
            _storageFile = StorageFile.GetFileFromPathAsync(FilePath).AsTask();
        }
        return _storageFile;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
