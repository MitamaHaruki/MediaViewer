using MediaViewer.Core.Diagnostics;

namespace MediaViewer.Core.Conversion;

/// <summary>変換前の調査結果。Notices は変換開始前に利用者へ見せる。</summary>
public sealed record MediaConversionPreparation(string InputPath, ConversionFormat Target, ProbeResult Probe, IReadOnlyList<string> Notices, bool Reencodes);

/// <summary>
/// 動画・音声の変換の流れ（仕様 §9.2, §9.3）：
/// 調査 → 一時ファイルへ変換 → 出力を検査 → 最終名へ確定。失敗・キャンセル時は一時ファイルを消し、
/// 壊れた完成ファイルを残さない。元ファイルは決して上書きしない。
/// </summary>
public static class MediaConverter
{
    public static async Task<MediaConversionPreparation> PrepareAsync(FfmpegTools tools, string inputPath, ConversionFormat target, CancellationToken cancellationToken)
    {
        var probe = await FfmpegRunner.ProbeAsync(tools, inputPath, cancellationToken);
        var plan = MediaConversionPlanner.Plan(probe, target, inputPath, "preview" + target.Extension());
        return new MediaConversionPreparation(inputPath, target, probe, plan.Notices, plan.ReencodesVideo || plan.ReencodesAudio);
    }

    public static async Task ExecuteAsync(FfmpegTools tools, MediaConversionPreparation prep, string outputPath, bool overwrite,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (ConversionPaths.IsSameFile(prep.InputPath, outputPath))
        {
            throw new InvalidOperationException("元のファイルは上書きできません。別の名前か保存先を指定してください。");
        }
        if (!overwrite && File.Exists(outputPath))
        {
            throw new IOException("同じ名前のファイルが既にあります。");
        }

        var temp = ConversionPaths.TemporaryFor(outputPath);
        var plan = MediaConversionPlanner.Plan(prep.Probe, prep.Target, prep.InputPath, temp);
        AppLog.Info($"変換開始：{prep.Target}（再エンコード：映像 {plan.ReencodesVideo}、音声 {plan.ReencodesAudio}）");
        try
        {
            await FfmpegRunner.RunAsync(tools, plan.Arguments, prep.Probe.Duration, progress, cancellationToken);
            await VerifyAsync(tools, temp, plan, prep.Probe.Duration, cancellationToken);
            File.Move(temp, outputPath, overwrite);
            AppLog.Info($"変換完了：{prep.Target}");
        }
        catch (FfmpegException e)
        {
            AppLog.Error($"変換失敗（終了コード {e.ExitCode}）：{e.Details}");
            throw;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>出力を ffprobe で調べ、期待したストリームと長さがあることを確かめる（壊れた出力を完成扱いにしない）。</summary>
    private static async Task VerifyAsync(FfmpegTools tools, string path, MediaConversionPlan plan, TimeSpan inputDuration, CancellationToken cancellationToken)
    {
        ProbeResult result;
        try
        {
            result = await FfmpegRunner.ProbeAsync(tools, path, cancellationToken);
        }
        catch (FfmpegException e)
        {
            throw new FfmpegException("変換後のファイルを確認できませんでした。", e.Details, e.ExitCode);
        }
        var video = result.Video.Count();
        var audio = result.Audio.Count();
        if (video < plan.ExpectedVideoStreams || audio < plan.ExpectedAudioStreams)
        {
            throw new FfmpegException("変換後のファイルに必要な映像・音声が含まれていません。",
                $"expected video={plan.ExpectedVideoStreams} audio={plan.ExpectedAudioStreams}, actual video={video} audio={audio}", 0);
        }
        if (inputDuration > TimeSpan.FromSeconds(1) && result.Duration < inputDuration * 0.9)
        {
            throw new FfmpegException("変換後のファイルの長さが元より短く、途中で終わっています。",
                $"input={inputDuration} output={result.Duration}", 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("変換の一時ファイルを削除できませんでした", e);
        }
    }
}
