using System.Buffers.Binary;
using System.Diagnostics;
using MediaViewer.Core.Conversion;
using MediaViewer.Tests.Color;
using Vortice.WIC;

namespace MediaViewer.Tests.Conversion;

/// <summary>
/// 実際の FFmpeg で変換する結合テスト。FFmpeg が見つからない環境では結論保留（Inconclusive）にする。
/// 入力はテスト内で作る（同梱する最小構成の FFmpeg でも動くようにする）。
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

        // 入力は、テスト内で作った非圧縮の映像・音声を FFmpeg で符号化して作る。同梱する FFmpeg は
        // 合成ソース（lavfi）や PNG・SRT の書き出しを含まない最小構成のため、それらに頼らない。

        // H.264 映像＋音声3本（AAC/Opus/FLAC、言語付き）＋SRT字幕の MKV（4秒）
        var srt = Path.Combine(s_dir, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nこんにちは\n");
        s_multiAudioMkv = Path.Combine(s_dir, "多重 音声 🎬.mkv");
        Ffmpeg([.. RawVideoInput(320, 240, 25, frames: 100),
            .. RawToneInput(440, 4), .. RawToneInput(660, 4), .. RawToneInput(880, 4),
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a:0", "aac", "-c:a:1", "libopus", "-c:a:2", "flac", "-c:s", "copy",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng", s_multiAudioMkv]);

        var cover = Path.Combine(s_dir, "cover.png");
        using var wic = new IWICImagingFactory();
        File.WriteAllBytes(cover, TestImageFiles.Encode(wic, ContainerFormat.Png, PixelFormat.Format24bppBGR, 200, 200, Pattern(200, 200, 0)));
        s_mp3WithArt = Path.Combine(s_dir, "曲.mp3");
        Ffmpeg([.. RawToneInput(523, 3), "-i", cover, "-map", "0:a", "-map", "1:v",
            "-c:a", "libmp3lame", "-c:v", "copy", "-disposition:v", "attached_pic", s_mp3WithArt]);
    }

    /// <summary>動きのある模様の非圧縮映像（RGB24）を書き、FFmpeg の入力指定を返す。</summary>
    private static string[] RawVideoInput(int width, int height, int fps, int frames)
    {
        var path = Path.Combine(s_dir, $"video-{width}x{height}-{frames}.rgb");
        using (var file = File.Create(path))
        {
            for (var f = 0; f < frames; f++) file.Write(Pattern(width, height, f));
        }
        return ["-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", $"{width}x{height}", "-framerate", $"{fps}", "-i", path];
    }

    private static byte[] Pattern(int width, int height, int frame)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                pixels[i] = (byte)(x + frame * 4);
                pixels[i + 1] = (byte)(y * 2 + frame);
                pixels[i + 2] = (byte)((x ^ y) + frame * 2);
            }
        }
        return pixels;
    }

    /// <summary>正弦波の非圧縮音声（48kHz、16bit、モノラル）を書き、FFmpeg の入力指定を返す。</summary>
    private static string[] RawToneInput(int frequency, double seconds)
    {
        const int rate = 48000;
        var path = Path.Combine(s_dir, $"tone-{frequency}-{seconds}.pcm");
        var samples = (int)(seconds * rate);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 8000));
        }
        File.WriteAllBytes(path, bytes);
        return ["-f", "s16le", "-ar", $"{rate}", "-ac", "1", "-i", path];
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
        // 1秒の 720p 映像を作り、繰り返して60秒にする（再エンコードに時間がかかる入力）
        var shortInput = Path.Combine(s_dir, "short.mkv");
        Ffmpeg([.. RawVideoInput(320, 180, 30, frames: 30), "-vf", "scale=1280:720", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", shortInput]);
        var longInput = Path.Combine(s_dir, "long.mkv");
        Ffmpeg("-stream_loop", "59", "-i", shortInput, "-c", "copy", longInput);
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
