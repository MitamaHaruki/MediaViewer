using System.Diagnostics;
using MediaViewer.Core.Conversion;

namespace MediaViewer.Tests.Conversion;

/// <summary>
/// 実際の FFmpeg で変換する結合テスト。FFmpeg が見つからない環境では結論保留（Inconclusive）にする。
/// 入力はテスト内で FFmpeg の合成ソースから作る。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MediaConverterIntegrationTests
{
    private static FfmpegTools s_tools = null!;
    private static string s_dir = null!;
    private static string s_multiAudioMkv = null!;
    private static string s_mp3WithArt = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_tools = FfmpegTools.Locate()!;
        if (s_tools is null) return;
        s_dir = Path.Combine(Path.GetTempPath(), "MediaViewerTests", "conv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(s_dir);

        // H.264 映像＋音声3本（AAC/Opus/FLAC、言語付き）＋SRT字幕の MKV（4秒）
        var srt = Path.Combine(s_dir, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nこんにちは\n");
        s_multiAudioMkv = Path.Combine(s_dir, "多重 音声 🎬.mkv");
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-f", "lavfi", "-i", "sine=frequency=660:duration=4", "-f", "lavfi", "-i", "sine=frequency=880:duration=4",
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a:0", "aac", "-c:a:1", "libopus", "-c:a:2", "flac", "-c:s", "srt",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng", s_multiAudioMkv);

        var cover = Path.Combine(s_dir, "cover.png");
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=200x200:duration=1", "-frames:v", "1", cover);
        s_mp3WithArt = Path.Combine(s_dir, "曲.mp3");
        Ffmpeg("-f", "lavfi", "-i", "sine=frequency=523:duration=3", "-i", cover, "-map", "0:a", "-map", "1:v",
            "-c:a", "libmp3lame", "-c:v", "png", "-disposition:v", "attached_pic", s_mp3WithArt);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (s_dir is not null) try { Directory.Delete(s_dir, true); } catch (IOException) { }
    }

    private static void RequireFfmpeg()
    {
        if (s_tools is null) Assert.Inconclusive("FFmpeg が見つからないため実行しません（tools/ffmpeg-dl または MEDIAVIEWER_FFMPEG_DIR）。");
    }

    private static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(s_tools.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(err);
    }

    private static async Task<(MediaConversionPreparation Prep, ProbeResult Output)> Convert(string input, ConversionFormat target)
    {
        var prep = await MediaConverter.PrepareAsync(s_tools, input, target, CancellationToken.None);
        var output = Path.Combine(s_dir, Path.GetFileNameWithoutExtension(input) + "-out" + target.Extension());
        double last = 0;
        await MediaConverter.ExecuteAsync(s_tools, prep, output, overwrite: true, new Progress<double>(p => last = p), CancellationToken.None);
        Assert.IsTrue(File.Exists(output));
        Assert.AreEqual(0, Directory.GetFiles(s_dir, "~*.converting-*").Length, "一時ファイルが残らない");
        return (prep, await FfmpegRunner.ProbeAsync(s_tools, output, CancellationToken.None));
    }

    [TestMethod]
    public async Task MKVからMP4へは再エンコードせず音声3本と字幕を保持する()
    {
        RequireFfmpeg();
        var (prep, output) = await Convert(s_multiAudioMkv, ConversionFormat.Mp4);

        Assert.AreEqual("h264", output.Video.Single().CodecName, "映像はコピー");
        var audio = output.Audio.ToList();
        Assert.AreEqual(3, audio.Count, "音声トラックはすべて残す");
        Assert.AreEqual("aac", audio[0].CodecName);
        Assert.AreEqual("opus", audio[1].CodecName, "MP4 は Opus を格納できるのでコピー");
        Assert.AreEqual("flac", audio[2].CodecName, "MP4 は FLAC を格納できるのでコピー");
        Assert.AreEqual("jpn", audio[0].Language, "言語情報を引き継ぐ");
        Assert.AreEqual("mov_text", output.Subtitles.Single().CodecName);
        Assert.IsFalse(prep.Reencodes);
        Assert.AreEqual(4, output.Duration.TotalSeconds, 0.3);
    }

    [TestMethod]
    public async Task MKVからWebMはVP9とOpusへ変換し再生可能な出力になる()
    {
        RequireFfmpeg();
        var (prep, output) = await Convert(s_multiAudioMkv, ConversionFormat.WebM);

        Assert.IsTrue(prep.Reencodes);
        Assert.AreEqual("vp9", output.Video.Single().CodecName);
        Assert.IsTrue(output.Audio.All(a => a.CodecName == "opus"));
        Assert.AreEqual(3, output.Audio.Count());
        Assert.AreEqual("webvtt", output.Subtitles.Single().CodecName);
    }

    [TestMethod]
    public async Task 音声のMP3からFLACへはアルバムアートを保持する()
    {
        RequireFfmpeg();
        var (_, output) = await Convert(s_mp3WithArt, ConversionFormat.Flac);
        Assert.AreEqual("flac", output.Audio.Single().CodecName);
        Assert.IsNotNull(output.CoverArt, "アルバムアートを保持する");
    }

    [TestMethod]
    public async Task キャンセルすると出力も一時ファイルも残らない()
    {
        RequireFfmpeg();
        var longInput = Path.Combine(s_dir, "long.mkv");
        Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=60", "-c:v", "libx264", "-preset", "ultrafast", longInput);
        var prep = await MediaConverter.PrepareAsync(s_tools, longInput, ConversionFormat.WebM, CancellationToken.None);
        var output = Path.Combine(s_dir, "long-cancel.webm");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var task = MediaConverter.ExecuteAsync(s_tools, prep, output, false, new Progress<double>(p => { if (p > 0) started.TrySetResult(); }), cts.Token);
        await Task.WhenAny(started.Task, Task.Delay(10000));
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => task);

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), $"キャンセルが速やかに効く（{sw.Elapsed.TotalSeconds:F1}秒）");
        Assert.IsFalse(File.Exists(output));
        Assert.AreEqual(0, Directory.GetFiles(s_dir, "~long-cancel.converting-*").Length);
    }

    [TestMethod]
    public async Task 元ファイルへの上書きは拒否する()
    {
        RequireFfmpeg();
        var prep = await MediaConverter.PrepareAsync(s_tools, s_multiAudioMkv, ConversionFormat.Mp4, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            MediaConverter.ExecuteAsync(s_tools, prep, s_multiAudioMkv, overwrite: true, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task 壊れたファイルは日本語のエラーになる()
    {
        RequireFfmpeg();
        var broken = Path.Combine(s_dir, "broken.mp4");
        File.WriteAllBytes(broken, [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 1, 2, 3]);
        var e = await Assert.ThrowsExactlyAsync<FfmpegException>(() =>
            MediaConverter.PrepareAsync(s_tools, broken, ConversionFormat.Mkv, CancellationToken.None));
        StringAssert.Contains(e.Message, "壊れている");
    }
}
