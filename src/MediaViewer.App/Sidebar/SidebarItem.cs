using System.ComponentModel;
using MediaViewer.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>サムネイルの読み込みを一度だけ行うための印。</summary>
    internal bool ThumbnailRequested { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
