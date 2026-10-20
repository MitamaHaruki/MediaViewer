using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaViewer.Core.Conversion;
using MediaViewer.Core.Diagnostics;
using MediaViewer.Core.Media;
using MediaViewer.Imaging.Conversion;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace MediaViewer.App;

/// <summary>
/// 形式変換の画面（仕様 §9.1）。入力・出力形式・保存先・開始／キャンセル・進捗だけを表示する。
/// 変換は非同期で行い、変換中もビューワーは操作できる（別ウィンドウ）。
/// </summary>
public sealed partial class ConversionWindow : Window
{
    private readonly Func<string, Task> _openInViewer;
    private readonly FfmpegTools? _tools = FfmpegTools.Locate();
    private string? _input;
    private MediaKind _kind;
    private IReadOnlyList<ConversionFormat> _targets = [];
    private object? _preparation;
    private CancellationTokenSource? _prepareCts;
    private CancellationTokenSource? _convertCts;
    private Task? _conversionTask;
    private bool _converting;
    private bool _closeAfterCancel;
    private string? _lastOutput;

    public ConversionWindow(string? inputPath, Func<string, Task> openInViewer)
    {
        _openInViewer = openInViewer;
        InitializeComponent();
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(640 * scale), (int)(620 * scale)));
        AppWindow.Closing += OnWindowClosing;

        if (inputPath is not null) _ = SetInputAsync(inputPath);
        else StatusText.Text = "変換するファイルを選んでください。";
    }

    /// <summary>入力ファイルを差し替える（変換中は受け付けない）。</summary>
    public void SetInput(string path)
    {
        if (!_converting) _ = SetInputAsync(path);
    }

    private async Task SetInputAsync(string path)
    {
        _input = path;
        InputBox.Text = path;
        FolderBox.Text = Path.GetDirectoryName(path) ?? "";
        ResetResult();
        StartButton.IsEnabled = false;
        NoticeList.ItemsSource = null;
        NoLossText.Visibility = Visibility.Collapsed;
        AnalyzingText.Visibility = Visibility.Visible;
        StatusText.Text = "";

        _kind = await DetectKindAsync(path);
        if (path != _input) return;
        if (_kind == MediaKind.Unknown)
        {
            AnalyzingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "このファイルは変換できません。対応していない形式か、ファイルが壊れている可能性があります。";
            FormatBox.ItemsSource = null;
            return;
        }
        if (_kind != MediaKind.Image && _tools is null)
        {
            AnalyzingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "動画・音声の変換に必要な FFmpeg が見つかりません。";
            FormatBox.ItemsSource = null;
            return;
        }
        _targets = ConversionFormats.TargetsFor(_kind, path);
        FormatBox.ItemsSource = _targets.Select(t => $"{t.DisplayName()}（{t.Extension()}）").ToList();
        FormatBox.SelectedIndex = Math.Max(0, _targets.ToList().IndexOf(DefaultTarget(_kind, path)));
    }

    /// <summary>よく使う出力形式を最初に選んでおく。</summary>
    private static ConversionFormat DefaultTarget(MediaKind kind, string path)
    {
        var current = ConversionFormats.FromPath(path);
        return kind switch
        {
            MediaKind.Image => current == ConversionFormat.Png ? ConversionFormat.Jpeg : ConversionFormat.Png,
            MediaKind.Video => current == ConversionFormat.Mp4 ? ConversionFormat.Mkv : ConversionFormat.Mp4,
            _ => current == ConversionFormat.Mp3 ? ConversionFormat.Flac : ConversionFormat.Mp3,
        };
    }

    /// <summary>拡張子だけで決めず、中身を調べて種類を判定する（映像のない MP4 は音声として扱う）。</summary>
    private async Task<MediaKind> DetectKindAsync(string path)
    {
        var byExtension = MediaKinds.FromPath(path);
        if (byExtension == MediaKind.Image) return MediaKind.Image;
        if (_tools is null) return byExtension;
        try
        {
            var probe = await FfmpegRunner.ProbeAsync(_tools, path, CancellationToken.None);
            if (probe.Video.Any() && byExtension != MediaKind.Audio) return MediaKind.Video;
            if (probe.Audio.Any()) return MediaKind.Audio;
            return byExtension == MediaKind.Unknown ? MediaKind.Unknown : byExtension;
        }
        catch (FfmpegException)
        {
            return byExtension == MediaKind.Video || byExtension == MediaKind.Audio ? byExtension : MediaKind.Unknown;
        }
    }

    private ConversionFormat? SelectedTarget =>
        FormatBox.SelectedIndex >= 0 && FormatBox.SelectedIndex < _targets.Count ? _targets[FormatBox.SelectedIndex] : null;

    private async void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_input is null || SelectedTarget is not { } target) return;
        FileNameBox.Text = Path.GetFileName(ConversionPaths.Suggest(_input, target));
        await PrepareAsync(_input, target);
    }

    /// <summary>変換前に入力を調べ、失われるもの・品質が変わるものを表示する（仕様 §9.2）。</summary>
    private async Task PrepareAsync(string input, ConversionFormat target)
    {
        _prepareCts?.Cancel();
        var cts = _prepareCts = new CancellationTokenSource();
        _preparation = null;
        StartButton.IsEnabled = false;
        ResetResult();
        AnalyzingText.Visibility = Visibility.Visible;
        NoticeList.ItemsSource = null;
        NoLossText.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        try
        {
            IReadOnlyList<string> notices;
            bool lossless;
            if (_kind == MediaKind.Image)
            {
                var prep = await Task.Run(() => ImageConverter.Prepare(input, target, cts.Token), cts.Token);
                _preparation = prep;
                notices = prep.Notices;
                lossless = notices.Count == 0;
            }
            else
            {
                var prep = await MediaConverter.PrepareAsync(_tools!, input, target, cts.Token);
                _preparation = prep;
                notices = prep.Notices;
                lossless = notices.Count == 0 && !prep.Reencodes;
            }
            if (cts.IsCancellationRequested) return;
            NoticeList.ItemsSource = notices.ToList();
            NoLossText.Visibility = lossless ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Warn("変換の準備に失敗しました", ex);
            StatusText.Text = Describe(ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested) AnalyzingText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOutputTextChanged(object sender, TextChangedEventArgs e) => ResetResult();

    // ---- 変換の実行 ----

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_input is null || _preparation is null) return;
        var folder = FolderBox.Text.Trim();
        var name = FileNameBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = "ファイル名に使えない文字が含まれているか、空です。";
            return;
        }
        if (!Directory.Exists(folder))
        {
            StatusText.Text = "保存先フォルダが見つかりません。";
            return;
        }
        var output = Path.Combine(folder, name);
        if (ConversionPaths.IsSameFile(_input, output))
        {
            // 元ファイルは上書きしない（仕様 §9.1）
            output = ConversionPaths.MakeUnique(output);
            FileNameBox.Text = Path.GetFileName(output);
        }
        var overwrite = false;
        if (File.Exists(output))
        {
            switch (await AskExistingAsync(Path.GetFileName(output)))
            {
                case ContentDialogResult.Primary:
                    overwrite = true;
                    break;
                case ContentDialogResult.Secondary:
                    output = ConversionPaths.MakeUnique(output);
                    FileNameBox.Text = Path.GetFileName(output);
                    break;
                default:
                    return;
            }
        }

        _converting = true;
        _convertCts = new CancellationTokenSource();
        SetConvertingUi(true);
        StatusText.Text = "変換しています...";
        var progress = new Progress<double>(p =>
        {
            Progress.Value = p;
            if (!_convertCts.IsCancellationRequested) StatusText.Text = $"変換しています... {p:P0}";
        });
        try
        {
            _conversionTask = _preparation switch
            {
                ImageConversionPreparation img => ImageConverter.ExecuteAsync(img, output, overwrite, _tools, progress, _convertCts.Token),
                MediaConversionPreparation media => MediaConverter.ExecuteAsync(_tools!, media, output, overwrite, progress, _convertCts.Token),
                _ => Task.CompletedTask,
            };
            await _conversionTask;
            _lastOutput = output;
            StatusText.Text = $"変換が完了しました：{Path.GetFileName(output)}";
            OpenFolderButton.Visibility = OpenResultButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "変換をキャンセルしました。途中のファイルは残していません。";
        }
        catch (Exception ex)
        {
            AppLog.Error("変換に失敗しました", ex);
            StatusText.Text = Describe(ex);
        }
        finally
        {
            _converting = false;
            SetConvertingUi(false);
            if (_closeAfterCancel) Close();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        FfmpegException f => f.Message + "（詳細はログに記録しました）",
        NotSupportedException or InvalidOperationException or InvalidDataException => ex.Message,
        UnauthorizedAccessException => "保存先に書き込む権限がありません。",
        IOException io => $"ファイルの読み書きに失敗しました：{io.Message}",
        SharpGen.Runtime.SharpGenException => "画像を読み書きできませんでした。対応していない形式か、ファイルが壊れている可能性があります。",
        _ => "変換に失敗しました。（詳細はログに記録しました）",
    };

    private async Task<ContentDialogResult> AskExistingAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "同じ名前のファイルがあります",
            Content = $"「{name}」は既にあります。どうしますか？",
            PrimaryButtonText = "上書き",
            SecondaryButtonText = "連番で保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Secondary,
            RequestedTheme = ElementTheme.Dark,
        };
        return await dialog.ShowAsync();
    }

    private void SetConvertingUi(bool converting)
    {
        Progress.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
        if (converting) Progress.Value = 0;
        StartButton.Visibility = converting ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        FormatBox.IsEnabled = BrowseInputButton.IsEnabled = BrowseFolderButton.IsEnabled = !converting;
        FolderBox.IsEnabled = FileNameBox.IsEnabled = !converting;
    }

    private void ResetResult()
    {
        if (_converting) return;
        _lastOutput = null;
        OpenFolderButton.Visibility = OpenResultButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _convertCts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "キャンセルしています...";
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_converting) return;
        args.Cancel = true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "変換中です",
            Content = "変換を中止して閉じますか？途中のファイルは残しません。",
            PrimaryButtonText = "中止して閉じる",
            CloseButtonText = "続ける",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _closeAfterCancel = true;
            _convertCts?.Cancel();
        }
    }

    // ---- ファイル・フォルダの選択 ----

    private async void OnBrowseInputClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add("*");
            var result = await picker.PickSingleFileAsync();
            if (result is not null && !string.IsNullOrEmpty(result.Path)) await SetInputAsync(result.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ファイルを選択できませんでした：{ex.Message}";
        }
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            if (result is not null && !string.IsNullOrEmpty(result.Path)) FolderBox.Text = result.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"フォルダを選択できませんでした：{ex.Message}";
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add($"/select,{_lastOutput}");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"フォルダを開けませんでした：{ex.Message}";
        }
    }

    private async void OnOpenResultClick(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null) await _openInViewer(_lastOutput);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
