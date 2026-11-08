using System.Runtime.InteropServices.WindowsRuntime;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Media;
using MediaViewer.Imaging.Conversion;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace MediaViewer.App;

/// <summary>
/// 画像の印刷（仕様決定事項 D15）。Windows の印刷画面（プレビュー付き）を使い、用紙の印刷できる範囲に
/// 縦横比を保って収め、中央に置く。画素は埋め込み ICC 等を反映した sRGB に変換してから渡す
/// （プリンターの色合わせは Windows とプリンタードライバーが sRGB として行う）。画像の表示中だけ使える。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>印刷に渡す画像の長辺の上限。A4 を 300dpi で印刷するのに足りる大きさに抑え、メモリを使い過ぎない。</summary>
    private const int PrintMaxDimension = 4096;

    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printSource;
    private WriteableBitmap? _printBitmap;
    private bool _printLandscape;
    private string _printTitle = "";
    private UIElement? _printPage;
    private bool _printing;

    private bool CanPrint => _currentKind == MediaKind.Image && _image is not null && _currentPath is not null && !_printing;

    private async void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (!CanPrint) return;
        if (!PrintManager.IsSupported())
        {
            ShowMessage("この PC では印刷できません。", InfoBarSeverity.Warning);
            return;
        }
        var path = _currentPath!;
        _printing = true;
        LoadingRing.IsActive = true;
        try
        {
            SrgbBitmap bitmap;
            try
            {
                bitmap = await Task.Run(() => ImageConverter.RenderSrgb(path, PrintMaxDimension, CancellationToken.None));
            }
            catch (NotSupportedException ex)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Warning);
                return;
            }
            if (path != _currentPath) return;

            var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
            bitmap.Pixels.CopyTo(writeable.PixelBuffer);
            writeable.Invalidate();
            _printBitmap = writeable;
            // 印刷の要求は画面とは別のスレッドで届き、そこでは画面の部品（WriteableBitmap）に触れられないため、向きはここで決めておく
            _printLandscape = bitmap.Width > bitmap.Height;
            _printTitle = Path.GetFileName(path);
            _printPage = null;

            RegisterForPrinting();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            LoadingRing.IsActive = false;
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Error("印刷を開始できませんでした。", ex);
            ShowMessage("印刷を開始できませんでした。プリンターの設定を確認してください。", InfoBarSeverity.Warning);
            ReleasePrintContent();
        }
        finally
        {
            LoadingRing.IsActive = false;
            _printing = false;
        }
    }

    /// <summary>このウィンドウを印刷の対象として登録する（最初の印刷のときに一度だけ）。</summary>
    private void RegisterForPrinting()
    {
        if (_printManager is not null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _printManager = PrintManagerInterop.GetForWindow(hwnd);
        _printManager.PrintTaskRequested += OnPrintTaskRequested;
        _printDocument = new PrintDocument();
        _printSource = _printDocument.DocumentSource;
        _printDocument.Paginate += OnPrintPaginate;
        _printDocument.GetPreviewPage += OnPrintGetPreviewPage;
        _printDocument.AddPages += OnPrintAddPages;
    }

    private void UnregisterForPrinting()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPrintPaginate;
            _printDocument.GetPreviewPage -= OnPrintGetPreviewPage;
            _printDocument.AddPages -= OnPrintAddPages;
        }
        if (_printManager is not null) _printManager.PrintTaskRequested -= OnPrintTaskRequested;
        _printManager = null;
        _printDocument = null;
        _printSource = null;
        ReleasePrintContent();
    }

    private void ReleasePrintContent()
    {
        _printBitmap = null;
        _printPage = null;
    }

    /// <summary>印刷画面を開いたとき（画面とは別のスレッドで呼ばれる）。</summary>
    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var source = _printSource;
        if (source is null) return;
        var task = args.Request.CreatePrintTask(_printTitle, a => a.SetSource(source));
        // 横長の画像は横向きの用紙を初期値にする（印刷画面で変更できる）
        task.Options.Orientation = _printLandscape ? PrintOrientation.Landscape : PrintOrientation.Portrait;
        task.Completed += (_, e) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (e.Completion == PrintTaskCompletion.Failed)
            {
                ShowMessage("印刷に失敗しました。プリンターの状態を確認してください。", InfoBarSeverity.Warning);
            }
            ReleasePrintContent();
        });
    }

    /// <summary>用紙の大きさ・向きが決まるたびに、1ページ分の内容を作り直す。</summary>
    private void OnPrintPaginate(object sender, PaginateEventArgs e)
    {
        if (_printBitmap is not { } bitmap) return;
        var options = (PrintTaskOptions)e.PrintTaskOptions;
        var page = options.GetPageDescription(0);
        var printable = page.ImageableRect;
        var root = new Grid { Width = page.PageSize.Width, Height = page.PageSize.Height };
        root.Children.Add(new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = printable.Width,
            Height = printable.Height,
            Margin = new Thickness(printable.X, printable.Y,
                Math.Max(0, page.PageSize.Width - printable.Right), Math.Max(0, page.PageSize.Height - printable.Bottom)),
        });
        _printPage = root;
        ((PrintDocument)sender).SetPreviewPageCount(1, PreviewPageCountType.Final);
    }

    private void OnPrintGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_printPage is { } page) ((PrintDocument)sender).SetPreviewPage(e.PageNumber, page);
    }

    private void OnPrintAddPages(object sender, AddPagesEventArgs e)
    {
        var document = (PrintDocument)sender;
        if (_printPage is { } page) document.AddPage(page);
        document.AddPagesComplete();
    }
}
