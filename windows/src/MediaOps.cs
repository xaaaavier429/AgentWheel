using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AgentWheel;

/// <summary>Video and audio, all through ffmpeg (bundled next to the app in the release zip).</summary>
public static class MediaOps
{
    // MARK: - Plumbing

    private static string Ffmpeg() => Shell.Ffmpeg ?? throw new FWError(Shell.InstallHint("ffmpeg"));

    private static async Task<string> Probe(string path, params string[] args)
    {
        string? probe = Shell.Ffprobe;
        if (probe == null) return "";
        try
        {
            var all = new List<string> { "-v", "error" };
            all.AddRange(args);
            all.Add(path);
            return await Shell.Run(probe, all);
        }
        catch
        {
            return "";
        }
    }

    public static async Task<double> Duration(string path)
    {
        string s = await Probe(path, "-show_entries", "format=duration", "-of", "default=nw=1:nk=1");
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0;
    }

    public static async Task<int> SampleRate(string path)
    {
        string s = await Probe(path, "-select_streams", "a:0", "-show_entries", "stream=sample_rate", "-of", "default=nw=1:nk=1");
        return int.TryParse(s.Trim(), out var r) && r > 0 ? r : 48_000;
    }

    /// <summary>Codec names of the first video and audio streams ("" when absent).</summary>
    public static async Task<(string video, string audio)> Codecs(string path)
    {
        string s = await Probe(path, "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0");
        string video = "", audio = "";
        foreach (var raw in s.Split('\n'))
        {
            var parts = raw.Trim().Split(',');
            if (parts.Length < 2) continue;
            string name = parts[0].Trim(), type = parts[1].Trim();
            if (type == "video" && video.Length == 0) video = name;
            if (type == "audio" && audio.Length == 0) audio = name;
        }
        return (video, audio);
    }

    public static async Task Ff(string input, IEnumerable<string> args, string output, ProgressFn progress, double? limit = null)
    {
        string exe = Ffmpeg();
        double total = await Duration(input);
        if (limit is double l && total > 0) total = Math.Min(total, l);
        var full = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", input };
        full.AddRange(args);
        full.Add(output);
        try
        {
            await Shell.Run(exe, full, null, line =>
            {
                if (total <= 0 || !line.StartsWith("out_time_us=")) return;
                if (double.TryParse(line.Substring("out_time_us=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var us))
                    progress(Math.Clamp(us / 1_000_000 / total, 0, 1));
            });
        }
        catch
        {
            Out.TryDelete(output);
            throw;
        }
    }

    public static string ScaleFilter(int maxSide) =>
        $"scale='if(gt(iw,ih),min({maxSide},iw),-2)':'if(gt(iw,ih),-2,min({maxSide},ih))':flags=lanczos";

    private static readonly string[] H264 =
    {
        "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
    };

    // MARK: - Video

    public static async Task<OpResult> Video(string id, string path, ProgressFn progress)
    {
        switch (id)
        {
            case "vid.to.mp4":
            case "vid.to.mov":
            {
                bool mp4 = id == "vid.to.mp4";
                string output = Out.Target(path, null, mp4 ? "mp4" : "mov");
                var (video, audio) = await Codecs(path);
                // Re-wrap without re-encoding when the codecs already fit the container.
                bool videoFits = video is "h264" or "hevc" || (!mp4 && video is "prores" or "mjpeg");
                bool audioFits = audio is "" or "aac" or "mp3" or "alac" || (!mp4 && audio.StartsWith("pcm_"));
                var args = videoFits && audioFits
                    ? new List<string> { "-map", "0:v:0", "-map", "0:a?", "-c", "copy" }
                    : new List<string>(H264);
                if (video == "hevc" && videoFits) args.AddRange(new[] { "-tag:v", "hvc1" });
                if (mp4) args.AddRange(new[] { "-movflags", "+faststart" });
                await Ff(path, args, output, progress);
                return new OpResult(output);
            }

            case "vid.to.hevc":
            {
                string output = Out.Target(path, "HEVC", "mp4");
                await Ff(path, new[] { "-c:v", "libx265", "-preset", "medium", "-crf", "26", "-pix_fmt", "yuv420p", "-tag:v", "hvc1",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" }, output, progress);
                return new OpResult(output);
            }

            case "vid.to.webm":
            {
                string output = Out.Target(path, null, "webm");
                await Ff(path, new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                    "-c:a", "libopus", "-b:a", "128k" }, output, progress);
                return new OpResult(output);
            }

            case "vid.to.gif":
            {
                string output = Out.Target(path, null, "gif");
                double length = await Duration(path);
                string graph = "fps=12," + ScaleFilter(540) +
                               ",split[a][b];[a]palettegen=max_colors=200:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";
                await Ff(path, new[] { "-t", "30", "-filter_complex", graph, "-loop", "0" }, output, progress, 30);
                return new OpResult(output) { Note = length > 30 ? "只取了前 30 秒" : null };
            }

            case "vid.to.mp3":
            {
                string output = Out.Target(path, null, "mp3");
                await Ff(path, new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" }, output, progress);
                return new OpResult(output);
            }

            case "vid.to.m4a":
            {
                string output = Out.Target(path, null, "m4a");
                await Ff(path, new[] { "-vn", "-c:a", "aac", "-b:a", "192k" }, output, progress);
                return new OpResult(output);
            }

            case "vid.to.wav":
            {
                string output = Out.Target(path, null, "wav");
                await Ff(path, new[] { "-vn", "-c:a", "pcm_s16le" }, output, progress);
                return new OpResult(output);
            }

            case "vid.compress":
            {
                string output = Out.Target(path, "壓縮", "mp4");
                await Ff(path, new[] { "-c:v", "libx264", "-preset", "faster", "-crf", "27", "-vf", ScaleFilter(1920), "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" }, output, progress);
                return ImageOps.ShrinkResult(path, output);
            }

            case "vid.mute":
            {
                string ext = Kinds.Ext(path) is "mp4" or "mov" or "m4v" or "mkv" or "webm" ? Kinds.Ext(path) : "mp4";
                string output = Out.Target(path, "靜音", ext);
                await Ff(path, new[] { "-map", "0:v", "-c", "copy", "-an" }, output, progress);
                return new OpResult(output);
            }

            case "vid.720p":
            {
                string output = Out.Target(path, "720P", "mp4");
                var args = new List<string> { "-vf", ScaleFilter(1280) };
                args.AddRange(H264);
                args.AddRange(new[] { "-movflags", "+faststart" });
                await Ff(path, args, output, progress);
                return new OpResult(output);
            }

            case "vid.cover":
            {
                string output = Out.Target(path, "封面", "jpg");
                double length = await Duration(path);
                double at = length > 2 ? Math.Min(1.0, length * 0.1) : 0;
                await Ff(path, new[] { "-ss", at.ToString(CultureInfo.InvariantCulture), "-frames:v", "1", "-q:v", "2" }, output, progress);
                return new OpResult(output);
            }

            case "vid.fast":
            case "vid.slow":
            {
                bool fast = id == "vid.fast";
                string output = Out.Target(path, fast ? "2倍速" : "慢放", "mp4");
                var (_, audio) = await Codecs(path);
                var args = new List<string> { "-filter:v", fast ? "setpts=0.5*PTS" : "setpts=2.0*PTS" };
                if (audio.Length > 0) args.AddRange(new[] { "-filter:a", fast ? "atempo=2.0" : "atempo=0.5" });
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" });
                await Ff(path, args, output, p => progress(fast ? Math.Min(1, p * 2) : p / 2));
                return new OpResult(output);
            }

            default:
                throw new FWError("未知操作 " + id);
        }
    }

    // MARK: - Audio

    /// <summary>Codec arguments that keep an audio file in its own format.</summary>
    private static (string ext, string[] args) SameFormatAudio(string path) => Catalog.CanonicalExt(Kinds.Ext(path)) switch
    {
        "mp3" => ("mp3", new[] { "-c:a", "libmp3lame", "-q:a", "2" }),
        "wav" => ("wav", new[] { "-c:a", "pcm_s16le" }),
        "aiff" => ("aiff", new[] { "-c:a", "pcm_s16be" }),
        "flac" => ("flac", new[] { "-c:a", "flac" }),
        "opus" => ("opus", new[] { "-c:a", "libopus", "-b:a", "128k" }),
        _ => ("m4a", new[] { "-c:a", "aac", "-b:a", "192k" }),
    };

    public static async Task<OpResult> Audio(string id, string path, ProgressFn progress)
    {
        async Task<OpResult> Simple(string suffix, string ext, params string[] codec)
        {
            string output = Out.Target(path, suffix.Length == 0 ? null : suffix, ext);
            var args = new List<string> { "-vn" };
            args.AddRange(codec);
            await Ff(path, args, output, progress);
            return new OpResult(output);
        }

        switch (id)
        {
            case "aud.to.mp3": return await Simple("", "mp3", "-c:a", "libmp3lame", "-q:a", "2");
            case "aud.to.m4a": return await Simple("", "m4a", "-c:a", "aac", "-b:a", "256k");
            case "aud.to.wav": return await Simple("", "wav", "-c:a", "pcm_s16le");
            case "aud.to.aiff": return await Simple("", "aiff", "-c:a", "pcm_s16be");
            case "aud.to.flac": return await Simple("", "flac", "-c:a", "flac");
            case "aud.to.opus": return await Simple("", "opus", "-c:a", "libopus", "-b:a", "128k");
            case "aud.to.alac": return await Simple("無損", "m4a", "-c:a", "alac");

            case "aud.to.m4r":
            {
                string output = Out.Target(path, "鈴聲", "m4r");
                double length = await Duration(path);
                await Ff(path, new[] { "-vn", "-t", "40", "-c:a", "aac", "-b:a", "256k", "-f", "ipod" }, output, progress, 40);
                return new OpResult(output) { Note = length > 40 ? "擷取了前 40 秒" : null };
            }

            case "aud.compress":
            {
                string output = Out.Target(path, "壓縮", "m4a");
                await Ff(path, new[] { "-vn", "-c:a", "aac", "-b:a", "96k" }, output, progress);
                return ImageOps.ShrinkResult(path, output);
            }

            case "aud.mono":
            case "aud.loud":
            case "aud.fast":
            {
                var same = SameFormatAudio(path);
                int rate = await SampleRate(path);
                (string suffix, string[] filter) = id switch
                {
                    "aud.mono" => ("單聲道", new[] { "-ac", "1" }),
                    // loudnorm upsamples to 192 kHz internally; write back at the source rate.
                    "aud.loud" => ("音量統一", new[] { "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", rate.ToString(CultureInfo.InvariantCulture) }),
                    _ => ("1.5倍速", new[] { "-af", "atempo=1.5" }),
                };
                string output = Out.Target(path, suffix, same.ext);
                var args = new List<string> { "-vn" };
                args.AddRange(filter);
                args.AddRange(same.args);
                await Ff(path, args, output, p => progress(id == "aud.fast" ? Math.Min(1, p * 1.5) : p));
                return new OpResult(output);
            }

            default:
                throw new FWError("未知操作 " + id);
        }
    }
}
