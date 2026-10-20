namespace MediaViewer.Core.Conversion;

/// <summary>FFmpeg による変換の手順（引数）と、変換前に利用者へ知らせる事項。</summary>
public sealed record MediaConversionPlan(
    ConversionFormat Target,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Notices,
    bool ReencodesVideo,
    bool ReencodesAudio,
    int ExpectedVideoStreams,
    int ExpectedAudioStreams);

/// <summary>
/// 動画・音声の変換手順を決める（仕様 §9.2）。
/// <list type="bullet">
/// <item>出力先が扱えるコーデックは再エンコードせずにコピー（remux）し、扱えないものだけ変換する。</item>
/// <item>ストリームは明示的に指定し、FFmpeg の自動選択に任せない。音声トラックは出力形式が扱える限りすべて残す。</item>
/// <item>失われるもの・品質が変わるものは、変換前に通知する。</item>
/// </list>
/// 細かな画質設定は持たず、形式ごとに標準的な設定を使う（仕様 §0-6）。
/// </summary>
public static class MediaConversionPlanner
{
    private enum SubtitleMode { Drop, CopyAll, ToMovText, ToWebVtt }

    private sealed record VideoEncoder(string Codec, string Label, string[] Options);

    private sealed record AudioEncoder(string Codec, string Label, string[] Options, bool Lossless);

    private sealed record Container(
        string[] VideoCopy,
        string[] AudioCopy,
        bool CopyAnyCodec,
        bool MultipleAudio,
        SubtitleMode Subtitles,
        bool Attachments,
        bool Chapters,
        bool KeepsCoverArt,
        VideoEncoder? VideoEncoder,
        AudioEncoder AudioEncoder,
        bool FastStart);

    private static readonly VideoEncoder H264 = new("libx264", "H.264", ["-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]);
    private static readonly VideoEncoder Vp9 = new("libvpx-vp9", "VP9", ["-crf", "32", "-b", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"]);
    private static readonly AudioEncoder Aac = new("aac", "AAC", ["-b", "256k"], false);
    private static readonly AudioEncoder Opus = new("libopus", "Opus", ["-b", "160k"], false);
    private static readonly AudioEncoder Mp3 = new("libmp3lame", "MP3", ["-q", "2"], false);
    private static readonly AudioEncoder Vorbis = new("libvorbis", "Vorbis", ["-q", "6"], false);
    private static readonly AudioEncoder Flac = new("flac", "FLAC", [], true);
    private static readonly AudioEncoder Pcm16 = new("pcm_s16le", "PCM 16bit", [], true);

    private static readonly string[] TextSubtitleCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"];

    private static readonly Dictionary<ConversionFormat, Container> Containers = new()
    {
        [ConversionFormat.Mp4] = new(["h264", "hevc", "av1", "mpeg4", "vp9"], ["aac", "mp3", "alac", "ac3", "eac3", "opus", "flac"],
            false, true, SubtitleMode.ToMovText, false, true, false, H264, Aac, true),
        [ConversionFormat.Mov] = new(["h264", "hevc", "mpeg4", "prores", "mjpeg"], ["aac", "mp3", "alac", "ac3", "eac3", "pcm_s16le", "pcm_s24le", "pcm_s16be"],
            false, true, SubtitleMode.ToMovText, false, true, false, H264, Aac, true),
        [ConversionFormat.Mkv] = new([], [], true, true, SubtitleMode.CopyAll, true, true, true, H264, Aac, false),
        [ConversionFormat.WebM] = new(["vp8", "vp9", "av1"], ["opus", "vorbis"],
            false, true, SubtitleMode.ToWebVtt, false, true, false, Vp9, Opus, false),
        [ConversionFormat.Avi] = new(["mpeg4", "h264", "mjpeg", "msmpeg4v2", "msmpeg4v3"], ["mp3", "ac3", "mp2", "pcm_s16le"],
            false, true, SubtitleMode.Drop, false, false, false, H264, Mp3, false),
        [ConversionFormat.Mp3] = new([], ["mp3"], false, false, SubtitleMode.Drop, false, false, true, null, Mp3, false),
        [ConversionFormat.Wav] = new([], ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_u8"], false, false, SubtitleMode.Drop, false, false, false, null, Pcm16, false),
        [ConversionFormat.Flac] = new([], ["flac"], false, false, SubtitleMode.Drop, false, false, true, null, Flac, false),
        [ConversionFormat.M4a] = new([], ["aac", "alac"], false, false, SubtitleMode.Drop, false, true, true, null, Aac, true),
        [ConversionFormat.Ogg] = new([], ["vorbis", "opus", "flac"], false, false, SubtitleMode.Drop, false, false, false, null, Vorbis, false),
    };

    private static readonly string[] LosslessAudioCodecs = ["flac", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_s16be", "pcm_u8", "wavpack", "ape", "tta", "truehd", "mlp"];

    public static bool IsSupportedTarget(ConversionFormat target) => Containers.ContainsKey(target);

    public static MediaConversionPlan Plan(ProbeResult probe, ConversionFormat target, string inputPath, string outputPath)
    {
        if (!Containers.TryGetValue(target, out var c))
        {
            throw new ArgumentException($"{target} は FFmpeg で変換する形式ではありません。", nameof(target));
        }
        var video = probe.Video.ToList();
        var audio = probe.Audio.ToList();
        var subs = probe.Subtitles.ToList();
        var isVideoTarget = target.Kind() == Media.MediaKind.Video;
        if (isVideoTarget && video.Count == 0) throw new ArgumentException("映像のないファイルは動画形式へ変換できません。");
        if (!isVideoTarget && audio.Count == 0) throw new ArgumentException("音声のないファイルは音声形式へ変換できません。");

        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-n", "-i", inputPath };
        var codecArgs = new List<string>();
        var notices = new List<string>();
        var reencodeVideo = false;
        var reencodeAudio = false;
        var outVideo = 0;
        var outAudio = 0;

        // ---- 映像 ----
        if (isVideoTarget)
        {
            foreach (var v in video)
            {
                args.AddRange(["-map", $"0:{v.Index}"]);
                if (c.CopyAnyCodec || c.VideoCopy.Contains(v.CodecName))
                {
                    codecArgs.AddRange([$"-c:v:{outVideo}", "copy"]);
                }
                else
                {
                    var enc = c.VideoEncoder!;
                    codecArgs.AddRange([$"-c:v:{outVideo}", enc.Codec]);
                    codecArgs.AddRange(PerStream(enc.Options, "v", outVideo));
                    reencodeVideo = true;
                }
                outVideo++;
            }
            if (reencodeVideo)
            {
                var label = c.VideoEncoder!.Label;
                notices.Add($"映像を {label} で再エンコードします。画質がわずかに変化し、変換に時間がかかります。"
                    + (c.VideoEncoder == Vp9 ? "（VP9 は特に時間がかかります）" : ""));
            }
        }

        // ---- 音声 ----
        var audioToMap = c.MultipleAudio ? audio : audio.Take(1).ToList();
        if (!c.MultipleAudio && audio.Count > 1)
        {
            notices.Add($"出力形式が複数の音声トラックに対応しないため、最初のトラックだけを変換します（{audio.Count} 本中 1 本）。");
        }
        var reencodedLabels = new HashSet<string>();
        foreach (var a in audioToMap)
        {
            args.AddRange(["-map", $"0:{a.Index}"]);
            if (c.CopyAnyCodec || c.AudioCopy.Contains(a.CodecName))
            {
                codecArgs.AddRange([$"-c:a:{outAudio}", "copy"]);
            }
            else
            {
                var enc = c.AudioEncoder;
                // WAV は元の量子化ビット数が16bitを超えるなら24bitで書き出す
                var codec = enc == Pcm16 && a.BitsPerSample > 16 ? "pcm_s24le" : enc.Codec;
                codecArgs.AddRange([$"-c:a:{outAudio}", codec]);
                codecArgs.AddRange(PerStream(enc.Options, "a", outAudio));
                if (enc == Opus && a.Channels > 2) codecArgs.AddRange([$"-mapping_family:a:{outAudio}", "1"]);
                reencodeAudio = true;
                reencodedLabels.Add(enc.Lossless ? $"{enc.Label}（可逆）" : enc.Label);
                if (enc.Lossless && !LosslessAudioCodecs.Contains(a.CodecName))
                {
                    notices.Add("元の音声が非可逆圧縮のため、可逆形式にしても音質は元と同じです（良くはなりません）。");
                }
            }
            outAudio++;
        }
        if (reencodeAudio)
        {
            var lossyOnly = reencodedLabels.All(l => !l.Contains("可逆"));
            notices.Add($"音声を {string.Join("・", reencodedLabels)} に変換します。" + (lossyOnly ? "音質がわずかに変化します。" : ""));
        }

        // ---- 字幕 ----
        if (subs.Count > 0)
        {
            switch (c.Subtitles)
            {
                case SubtitleMode.CopyAll:
                    foreach (var s in subs) args.AddRange(["-map", $"0:{s.Index}"]);
                    codecArgs.AddRange(["-c:s", "copy"]);
                    break;
                case SubtitleMode.ToMovText or SubtitleMode.ToWebVtt:
                    var text = subs.Where(s => TextSubtitleCodecs.Contains(s.CodecName)).ToList();
                    foreach (var s in text) args.AddRange(["-map", $"0:{s.Index}"]);
                    if (text.Count > 0)
                    {
                        codecArgs.AddRange(["-c:s", c.Subtitles == SubtitleMode.ToMovText ? "mov_text" : "webvtt"]);
                        if (text.Any(s => s.CodecName is "ass" or "ssa"))
                            notices.Add("字幕の書式（色・位置・フォント）は出力形式で扱えないため失われます。文字は残ります。");
                    }
                    if (subs.Count > text.Count)
                        notices.Add($"画像形式の字幕（{subs.Count - text.Count} 本）は出力形式で扱えないため失われます。");
                    break;
                default:
                    notices.Add($"字幕（{subs.Count} 本）は出力形式で扱えないため失われます。");
                    break;
            }
        }

        // ---- 添付ファイル（フォント等）・アルバムアート ----
        var attachments = probe.Attachments.ToList();
        if (attachments.Count > 0)
        {
            if (c.Attachments)
            {
                args.AddRange(["-map", "0:t?"]);
                codecArgs.AddRange(["-c:t", "copy"]);
            }
            else
            {
                notices.Add($"添付ファイル（フォント等 {attachments.Count} 件）は出力形式で扱えないため失われます。");
            }
        }
        if (probe.CoverArt is { } cover)
        {
            if (!isVideoTarget && c.KeepsCoverArt)
            {
                args.AddRange(["-map", $"0:{cover.Index}"]);
                codecArgs.AddRange(["-c:v", "copy", "-disposition:v:0", "attached_pic"]);
            }
            else if (isVideoTarget && c.CopyAnyCodec)
            {
                // 映像ストリームの後ろに並ぶ。指定しないと既定のエンコーダーで再エンコードされるためコピーを明示する
                args.AddRange(["-map", $"0:{cover.Index}"]);
                codecArgs.AddRange([$"-c:v:{outVideo}", "copy"]);
            }
            else
            {
                notices.Add("アルバムアート（埋め込み画像）は出力形式で保存できないため失われます。");
            }
        }

        args.AddRange(codecArgs);
        // メタデータ（タイトル・言語等）とチャプターを引き継ぐ
        args.AddRange(["-map_metadata", "0"]);
        if (c.Chapters) args.AddRange(["-map_chapters", "0"]);
        if (target == ConversionFormat.Mp3) args.AddRange(["-id3v2_version", "3"]);
        if (c.FastStart) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", target.FfmpegMuxer(), outputPath]);

        return new MediaConversionPlan(target, args, notices.Distinct().ToList(), reencodeVideo, reencodeAudio, outVideo, outAudio);
    }

    /// <summary>エンコーダーの設定を、出力ストリーム番号付きの指定（-crf:v:0 など）にする。</summary>
    private static IEnumerable<string> PerStream(string[] options, string type, int index)
    {
        for (var i = 0; i + 1 < options.Length; i += 2)
        {
            yield return $"{options[i]}:{type}:{index}";
            yield return options[i + 1];
        }
    }
}
