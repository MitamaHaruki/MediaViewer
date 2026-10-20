using MediaViewer.Core.Conversion;
using MediaViewer.Core.Media;

namespace MediaViewer.Tests.Conversion;

[TestClass]
public sealed class MediaConversionPlannerTests
{
    private static ProbeStream V(int i, string codec) => new(i, "video", codec, 1920, 1080, 0, 8, false, null, null);
    private static ProbeStream A(int i, string codec, int ch = 2, int bits = 0, string? lang = null) => new(i, "audio", codec, 0, 0, ch, bits, false, lang, null);
    private static ProbeStream S(int i, string codec) => new(i, "subtitle", codec, 0, 0, 0, 0, false, null, null);
    private static ProbeStream Cover(int i) => new(i, "video", "mjpeg", 600, 600, 0, 8, true, null, null);
    private static ProbeStream Font(int i) => new(i, "attachment", "ttf", 0, 0, 0, 0, false, null, null);

    private static ProbeResult Probe(params ProbeStream[] s) => new("matroska,webm", TimeSpan.FromSeconds(60), s);

    private static string Arg(MediaConversionPlan p, string key) => p.Arguments[p.Arguments.ToList().IndexOf(key) + 1];

    private static List<string> Maps(MediaConversionPlan p) =>
        p.Arguments.Select((a, i) => (a, i)).Where(x => x.a == "-map").Select(x => p.Arguments[x.i + 1]).ToList();

    [TestMethod]
    public void MKVからMP4へは対応コーデックを再エンコードせずにコピーする()
    {
        var p = MediaConversionPlanner.Plan(Probe(V(0, "h264"), A(1, "aac"), A(2, "aac")), ConversionFormat.Mp4, "in.mkv", "out.mp4");

        Assert.IsFalse(p.ReencodesVideo);
        Assert.IsFalse(p.ReencodesAudio);
        Assert.AreEqual("copy", Arg(p, "-c:v:0"));
        Assert.AreEqual("copy", Arg(p, "-c:a:0"));
        Assert.AreEqual("copy", Arg(p, "-c:a:1"));
        CollectionAssert.AreEqual(new[] { "0:0", "0:1", "0:2" }, Maps(p));
        Assert.AreEqual(2, p.ExpectedAudioStreams);
        Assert.AreEqual(0, p.Notices.Count);
        Assert.AreEqual("mp4", Arg(p, "-f"));
        CollectionAssert.Contains(p.Arguments.ToList(), "+faststart");
    }

    [TestMethod]
    public void 出力形式が扱えないコーデックだけを再エンコードし通知する()
    {
        // 音声：AAC はコピー、Opus と FLAC は MOV に入らないので AAC へ
        var p = MediaConversionPlanner.Plan(Probe(V(0, "vp9"), A(1, "aac"), A(2, "opus"), A(3, "flac")), ConversionFormat.Mov, "in.mkv", "out.mov");

        Assert.AreEqual("libx264", Arg(p, "-c:v:0"));
        Assert.AreEqual("18", Arg(p, "-crf:v:0"));
        Assert.AreEqual("copy", Arg(p, "-c:a:0"));
        Assert.AreEqual("aac", Arg(p, "-c:a:1"));
        Assert.AreEqual("aac", Arg(p, "-c:a:2"));
        Assert.AreEqual(3, p.ExpectedAudioStreams);
        Assert.IsTrue(p.Notices.Any(n => n.Contains("映像を H.264")));
        Assert.IsTrue(p.Notices.Any(n => n.Contains("音声を AAC")));
    }

    [TestMethod]
    public void WebMはVP9とOpusへ変換する()
    {
        var p = MediaConversionPlanner.Plan(Probe(V(0, "h264"), A(1, "aac", ch: 6)), ConversionFormat.WebM, "in.mp4", "out.webm");
        Assert.AreEqual("libvpx-vp9", Arg(p, "-c:v:0"));
        Assert.AreEqual("libopus", Arg(p, "-c:a:0"));
        Assert.AreEqual("1", Arg(p, "-mapping_family:a:0"), "5.1ch の Opus はマッピングファミリー1が必要");
        Assert.IsTrue(p.Notices.Any(n => n.Contains("VP9 は特に時間がかかります")));
    }

    [TestMethod]
    public void 音声形式は最初の音声トラックだけを変換し事前に通知する()
    {
        var p = MediaConversionPlanner.Plan(Probe(A(0, "aac", lang: "jpn"), A(1, "opus", lang: "eng")), ConversionFormat.Mp3, "in.mka", "out.mp3");
        CollectionAssert.AreEqual(new[] { "0:0" }, Maps(p));
        Assert.AreEqual("libmp3lame", Arg(p, "-c:a:0"));
        Assert.IsTrue(p.Notices.Any(n => n.Contains("最初のトラックだけ")));
        Assert.AreEqual("3", Arg(p, "-id3v2_version"));
    }

    [TestMethod]
    public void 字幕は形式に応じて変換または通知する()
    {
        var probe = Probe(V(0, "h264"), A(1, "aac"), S(2, "subrip"), S(3, "ass"), S(4, "hdmv_pgs_subtitle"));

        var mp4 = MediaConversionPlanner.Plan(probe, ConversionFormat.Mp4, "in.mkv", "out.mp4");
        CollectionAssert.AreEqual(new[] { "0:0", "0:1", "0:2", "0:3" }, Maps(mp4));
        Assert.AreEqual("mov_text", Arg(mp4, "-c:s"));
        Assert.IsTrue(mp4.Notices.Any(n => n.Contains("書式")));
        Assert.IsTrue(mp4.Notices.Any(n => n.Contains("画像形式の字幕（1 本）")));

        var avi = MediaConversionPlanner.Plan(probe, ConversionFormat.Avi, "in.mkv", "out.avi");
        Assert.IsTrue(avi.Notices.Any(n => n.Contains("字幕（3 本）")));
        Assert.IsFalse(Maps(avi).Contains("0:2"));

        var mkv = MediaConversionPlanner.Plan(probe, ConversionFormat.Mkv, "in.mp4", "out.mkv");
        Assert.AreEqual("copy", Arg(mkv, "-c:s"));
        Assert.AreEqual(5, Maps(mkv).Count);
    }

    [TestMethod]
    public void 添付ファイルはMKVだけが保持する()
    {
        var probe = Probe(V(0, "h264"), A(1, "aac"), Font(2));
        Assert.IsTrue(Maps(MediaConversionPlanner.Plan(probe, ConversionFormat.Mkv, "in.mkv", "out.mkv")).Contains("0:t?"));
        Assert.IsTrue(MediaConversionPlanner.Plan(probe, ConversionFormat.Mp4, "in.mkv", "out.mp4").Notices.Any(n => n.Contains("添付ファイル")));
    }

    [TestMethod]
    public void アルバムアートは保持できる形式ではコピーし_できない形式では通知する()
    {
        var probe = Probe(A(0, "mp3"), Cover(1));
        var flac = MediaConversionPlanner.Plan(probe, ConversionFormat.Flac, "in.mp3", "out.flac");
        CollectionAssert.AreEqual(new[] { "0:0", "0:1" }, Maps(flac));
        Assert.AreEqual("attached_pic", Arg(flac, "-disposition:v:0"));
        Assert.IsTrue(flac.Notices.Any(n => n.Contains("非可逆圧縮")), "MP3→FLAC は音質が良くならないことを知らせる");

        var ogg = MediaConversionPlanner.Plan(probe, ConversionFormat.Ogg, "in.mp3", "out.ogg");
        Assert.IsTrue(ogg.Notices.Any(n => n.Contains("アルバムアート")));
        Assert.AreEqual(1, Maps(ogg).Count);
    }

    [TestMethod]
    public void WAVは元の量子化ビット数に合わせる()
    {
        var p16 = MediaConversionPlanner.Plan(Probe(A(0, "flac", bits: 16)), ConversionFormat.Wav, "in.flac", "out.wav");
        Assert.AreEqual("pcm_s16le", Arg(p16, "-c:a:0"));
        var p24 = MediaConversionPlanner.Plan(Probe(A(0, "flac", bits: 24)), ConversionFormat.Wav, "in.flac", "out.wav");
        Assert.AreEqual("pcm_s24le", Arg(p24, "-c:a:0"));
    }

    [TestMethod]
    public void ファイル名は引数の1要素として渡しシェルに連結しない()
    {
        const string tricky = "C:\\動画 & テスト\\\"quoted\" ; rm -rf 🎉.mkv";
        var p = MediaConversionPlanner.Plan(Probe(V(0, "h264"), A(1, "aac")), ConversionFormat.Mp4, tricky, "out.mp4");
        Assert.AreEqual(tricky, Arg(p, "-i"));
        Assert.AreEqual("out.mp4", p.Arguments[^1]);
        CollectionAssert.Contains(p.Arguments.ToList(), "-n", "既存ファイルを上書きしない");
    }

    [TestMethod]
    public void 種類の合わない変換は受け付けない()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            MediaConversionPlanner.Plan(Probe(A(0, "aac")), ConversionFormat.Mp4, "in.m4a", "out.mp4"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            MediaConversionPlanner.Plan(Probe(V(0, "h264")), ConversionFormat.Mp3, "in.mp4", "out.mp3"));
    }

    [TestMethod]
    public void 入力と同じ形式は選択肢に出さない()
    {
        var targets = ConversionFormats.TargetsFor(MediaKind.Video, "movie.MKV");
        Assert.IsFalse(targets.Contains(ConversionFormat.Mkv));
        CollectionAssert.IsSubsetOf(new[] { ConversionFormat.Mp4, ConversionFormat.WebM }, targets.ToList());
        Assert.IsTrue(targets.All(t => t.Kind() == MediaKind.Video));
    }
}

[TestClass]
public sealed class ConversionSupportTests
{
    [TestMethod]
    public void ffprobeのJSONを読む()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1280, "height": 720, "disposition": { "attached_pic": 0 } },
                { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2, "tags": { "language": "jpn", "title": "日本語" } },
                { "index": 2, "codec_name": "mjpeg", "codec_type": "video", "disposition": { "attached_pic": 1 } },
                { "index": 3, "codec_name": "flac", "codec_type": "audio", "bits_per_raw_sample": "24" }
              ],
              "format": { "format_name": "matroska,webm", "duration": "20.016000" }
            }
            """;
        var r = ProbeResult.Parse(json);
        Assert.AreEqual(TimeSpan.FromSeconds(20.016), r.Duration);
        Assert.AreEqual(1, r.Video.Count());
        Assert.AreEqual(2, r.Audio.Count());
        Assert.AreEqual(2, r.CoverArt!.Index);
        Assert.AreEqual("jpn", r.Audio.First().Language);
        Assert.AreEqual("日本語", r.Audio.First().Title);
        Assert.AreEqual(24, r.Audio.Last().BitsPerSample);
    }

    [TestMethod]
    [DataRow("out_time_us=15000000", 0.25)]
    [DataRow("out_time_us=-5", -1.0)]
    [DataRow("progress=end", 1.0)]
    [DataRow("frame=12", -1.0)]
    public void 進捗の行を読む(string line, double expected)
    {
        var ok = FfmpegRunner.TryParseProgress(line, TimeSpan.FromSeconds(60), out var p);
        if (expected < 0) Assert.IsFalse(ok);
        else Assert.AreEqual(expected, p, 1e-9);
    }

    [TestMethod]
    public void 出力名は拡張子だけを変え_既存なら連番にする()
    {
        var suggested = ConversionPaths.Suggest(@"C:\写真\旅行 1.mkv", ConversionFormat.Mp4);
        Assert.AreEqual(@"C:\写真\旅行 1.mp4", suggested);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\写真\旅行 1.mp4", @"C:\写真\旅行 1 (2).mp4" };
        Assert.AreEqual(@"C:\写真\旅行 1 (3).mp4", ConversionPaths.MakeUnique(suggested, existing.Contains));
        Assert.IsTrue(ConversionPaths.IsSameFile(@"C:\写真\a.MP4", @"c:\写真\.\a.mp4"));

        var temp = ConversionPaths.TemporaryFor(suggested);
        Assert.AreEqual(@"C:\写真", Path.GetDirectoryName(temp), "一時ファイルは同じフォルダ（置き換えが確実に行える）");
        Assert.AreEqual(".mp4", Path.GetExtension(temp));
    }
}
