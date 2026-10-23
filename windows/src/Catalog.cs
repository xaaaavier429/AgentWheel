using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;

namespace AgentWheel;

public enum FileKind
{
    image,
    pdf,
    video,
    audio,
    document,
    other,
}

public static class Kinds
{
    public static string Title(FileKind kind) => kind switch
    {
        FileKind.image => "圖片",
        FileKind.pdf => "PDF",
        FileKind.video => "影片",
        FileKind.audio => "音訊",
        FileKind.document => "文件",
        _ => "檔案",
    };

    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jpe", "jfif", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "avif", "ico",
        "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "psd", "tga", "jxl",
    };

    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "avi", "mkv", "webm", "flv", "wmv", "ts", "m2ts", "mts", "3gp", "ogv", "mpg", "mpeg", "vob",
    };

    public static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "aac", "wav", "flac", "aiff", "aif", "aifc", "opus", "ogg", "oga", "wma", "ape", "amr", "m4r", "caf", "alac",
    };

    public static readonly HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "rtf", "html", "htm", "doc", "docx", "odt",
    };

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.other;
        string ext = Ext(path);
        if (ext == "pdf") return FileKind.pdf;
        if (DocumentExts.Contains(ext)) return FileKind.document;
        if (ImageExts.Contains(ext)) return FileKind.image;
        if (VideoExts.Contains(ext)) return FileKind.video;
        if (AudioExts.Contains(ext)) return FileKind.audio;
        return FileKind.other;
    }
}

/// <summary>One slot on the wheel.</summary>
public sealed class WheelOption
{
    public string Id;
    /// <summary>Big label drawn in the slot (format name).</summary>
    public string? Big;
    /// <summary>Small caption under the icon — the slot's "ammo count".</summary>
    public string Ammo;
    public string Title;
    public string Detail;
    public bool Enabled = true;
    /// <summary>Why the slot is unavailable.</summary>
    public string? Note;
    /// <summary>Stat bar values (0…1) after the action, compared with the source's.</summary>
    public double[]? Stats;

    public WheelOption(string id, string? big, string ammo, string title, string detail, double[]? stats = null)
    {
        Id = id;
        Big = big;
        Ammo = ammo;
        Title = title;
        Detail = detail;
        Stats = stats;
    }

    public WheelOption Disable(string note, string? ammo = null)
    {
        Enabled = false;
        Note = note;
        if (ammo != null) Ammo = ammo;
        return this;
    }
}

public sealed class WheelPage
{
    public string Name;
    public List<WheelOption?> Slots;

    public WheelPage(string name, IEnumerable<WheelOption?> slots)
    {
        Name = name;
        Slots = slots.ToList();
    }
}

public sealed class WheelContext
{
    public List<string> Files = new();
    public FileKind Kind;
    public List<WheelPage> Pages = new();
    public string Headline = "";
    public string Summary = "";
    public string[] StatLabels = Array.Empty<string>();
    public double[] Base = Array.Empty<double>();
}

public static class Catalog
{
    public static WheelContext Context(IList<string> files)
    {
        var kinds = files.Select(Kinds.Of).ToList();
        // The wheel follows the most common kind among the dragged files.
        FileKind kind = files.Count == 0
            ? FileKind.other
            : kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).First().Key;
        var sameKind = files.Where(f => Kinds.Of(f) == kind).ToList();
        var exts = sameKind.Select(f => CanonicalExt(Kinds.Ext(f))).Distinct().ToList();
        string? current = exts.Count == 1 ? exts[0] : null;

        var ctx = new WheelContext { Files = files.ToList(), Kind = kind };
        switch (kind)
        {
            case FileKind.image:
                ctx.StatLabels = new[] { "畫質", "壓縮率", "相容性", "透明" };
                ctx.Base = Traits(ImageTraits, current);
                ctx.Pages = new() { ImageConvert(current, sameKind.Count), ImageTools(ctx.Base) };
                break;
            case FileKind.pdf:
                ctx.StatLabels = new[] { "版式還原", "可編輯", "體積小", "相容性" };
                ctx.Base = PdfTraits["pdf"];
                ctx.Pages = new() { PdfConvert(), PdfTools(ctx.Base, sameKind.Count) };
                break;
            case FileKind.video:
                ctx.StatLabels = new[] { "畫質", "壓縮率", "相容性" };
                ctx.Base = Traits(VideoTraits, current);
                ctx.Pages = new() { VideoConvert(current), VideoTools(ctx.Base) };
                break;
            case FileKind.audio:
                ctx.StatLabels = new[] { "音質", "壓縮率", "相容性" };
                ctx.Base = Traits(AudioTraits, current);
                ctx.Pages = new() { AudioConvert(current), AudioTools(ctx.Base) };
                break;
            case FileKind.document:
                ctx.StatLabels = new[] { "排版還原", "可編輯", "相容性" };
                ctx.Base = Traits(DocTraits, current);
                ctx.Pages = new() { DocConvert(current), DocTools(exts.Count == 1 && exts[0] == "docx") };
                if (exts.Contains("doc") && !Office.Available)
                {
                    // Old binary .doc can only be read through Word or LibreOffice.
                    foreach (var page in ctx.Pages)
                        foreach (var slot in page.Slots)
                            if (slot != null && !slot.Id.StartsWith("gen.")) slot.Disable(Office.Hint, "未裝備");
                }
                break;
            default:
                ctx.Pages = new() { GenericPage(files) };
                break;
        }

        if (files.Count == 1)
        {
            ctx.Headline = Path.GetFileName(files[0]);
            ctx.Summary = Describe(files[0], kind);
        }
        else
        {
            ctx.Headline = files.Count + " 個檔案";
            long total = files.Sum(f => Fmt.Size(f));
            int skipped = files.Count - sameKind.Count;
            ctx.Summary = Fmt.Bytes(total) + " · " + Kinds.Title(kind) + (skipped > 0 ? " · 另有 " + skipped + " 個會跳過" : "");
        }
        return ctx;
    }

    /// <summary>Every option id the engine understands, for --run and tests.</summary>
    public static List<string> AllIds
    {
        get
        {
            var pages = new List<WheelPage>
            {
                ImageConvert(null, 1), ImageTools(ImageTraits["png"]),
                PdfConvert(), PdfTools(PdfTraits["pdf"], 2),
                VideoConvert(null), VideoTools(VideoTraits["mp4"]),
                AudioConvert(null), AudioTools(AudioTraits["mp3"]),
                DocConvert(null), DocTools(true),
                GenericPage(Array.Empty<string>()),
            };
            return pages.SelectMany(p => p.Slots).Where(s => s != null).Select(s => s!.Id).Distinct().ToList();
        }
    }

    public static string CanonicalExt(string ext) => ext.ToLowerInvariant() switch
    {
        "jpeg" or "jpe" or "jfif" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "htm" => "html",
        "markdown" => "md",
        "aif" or "aifc" => "aiff",
        "oga" or "ogg" => "opus",
        var e => e,
    };

    private static string Describe(string path, FileKind kind)
    {
        var parts = new List<string> { Fmt.Bytes(Fmt.Size(path)) };
        if (kind == FileKind.image)
        {
            try
            {
                var info = new MagickImageInfo(path);
                parts.Add(info.Width + "×" + info.Height);
            }
            catch
            {
                // unreadable header: skip the size
            }
        }
        string ext = Kinds.Ext(path).ToUpperInvariant();
        parts.Add(ext.Length == 0 ? Kinds.Title(kind) : ext);
        return string.Join(" · ", parts);
    }

    // MARK: - Stats tables

    public static readonly Dictionary<string, double[]> ImageTraits = new()
    {
        ["png"] = new[] { 1.00, 0.35, 1.00, 1.00 },
        ["jpg"] = new[] { 0.74, 0.78, 1.00, 0.00 },
        ["heic"] = new[] { 0.84, 0.90, 0.55, 1.00 },
        ["webp"] = new[] { 0.80, 0.86, 0.82, 1.00 },
        ["avif"] = new[] { 0.86, 0.95, 0.60, 0.00 },
        ["pdf"] = new[] { 1.00, 0.40, 0.95, 1.00 },
        ["gif"] = new[] { 0.30, 0.55, 1.00, 0.50 },
        ["tiff"] = new[] { 1.00, 0.20, 0.70, 1.00 },
        ["bmp"] = new[] { 1.00, 0.05, 0.85, 0.00 },
        ["other"] = new[] { 0.92, 0.30, 0.45, 0.50 },
    };

    public static readonly Dictionary<string, double[]> PdfTraits = new()
    {
        ["pdf"] = new[] { 1.00, 0.15, 0.60, 0.95 },
        ["png"] = new[] { 0.95, 0.00, 0.40, 1.00 },
        ["jpg"] = new[] { 0.88, 0.00, 0.72, 1.00 },
        ["long"] = new[] { 0.90, 0.00, 0.62, 1.00 },
        ["docx"] = new[] { 0.55, 0.95, 0.70, 0.90 },
        ["txt"] = new[] { 0.08, 1.00, 1.00, 1.00 },
        ["md"] = new[] { 0.35, 1.00, 0.95, 0.80 },
        ["rtf"] = new[] { 0.50, 0.90, 0.60, 0.75 },
        ["html"] = new[] { 0.45, 0.80, 0.75, 0.95 },
    };

    public static readonly Dictionary<string, double[]> VideoTraits = new()
    {
        ["mp4"] = new[] { 0.85, 0.70, 1.00 },
        ["m4v"] = new[] { 0.85, 0.70, 0.90 },
        ["mov"] = new[] { 0.95, 0.45, 0.75 },
        ["hevc"] = new[] { 0.90, 0.88, 0.70 },
        ["webm"] = new[] { 0.82, 0.85, 0.70 },
        ["mkv"] = new[] { 0.90, 0.72, 0.50 },
        ["avi"] = new[] { 0.70, 0.40, 0.55 },
        ["gif"] = new[] { 0.20, 0.10, 1.00 },
        ["mp3"] = new[] { 0.00, 0.97, 1.00 },
        ["m4a"] = new[] { 0.00, 0.96, 0.92 },
        ["wav"] = new[] { 0.00, 0.80, 0.95 },
        ["other"] = new[] { 0.80, 0.60, 0.50 },
    };

    public static readonly Dictionary<string, double[]> AudioTraits = new()
    {
        ["mp3"] = new[] { 0.70, 0.80, 1.00 },
        ["m4a"] = new[] { 0.80, 0.82, 0.92 },
        ["aac"] = new[] { 0.78, 0.82, 0.85 },
        ["wav"] = new[] { 1.00, 0.00, 0.95 },
        ["aiff"] = new[] { 1.00, 0.00, 0.70 },
        ["flac"] = new[] { 1.00, 0.50, 0.65 },
        ["opus"] = new[] { 0.82, 0.92, 0.55 },
        ["alac"] = new[] { 1.00, 0.45, 0.70 },
        ["m4r"] = new[] { 0.80, 0.82, 0.40 },
        ["caf"] = new[] { 0.95, 0.30, 0.40 },
        ["other"] = new[] { 0.75, 0.60, 0.50 },
    };

    public static readonly Dictionary<string, double[]> DocTraits = new()
    {
        ["pdf"] = new[] { 1.00, 0.15, 1.00 },
        ["docx"] = new[] { 0.85, 1.00, 0.95 },
        ["doc"] = new[] { 0.75, 0.90, 0.85 },
        ["txt"] = new[] { 0.10, 1.00, 1.00 },
        ["md"] = new[] { 0.40, 1.00, 0.75 },
        ["html"] = new[] { 0.70, 0.80, 0.95 },
        ["rtf"] = new[] { 0.75, 0.90, 0.80 },
        ["odt"] = new[] { 0.80, 0.95, 0.60 },
        ["other"] = new[] { 0.60, 0.80, 0.60 },
    };

    private static double[] Traits(Dictionary<string, double[]> table, string? key) =>
        key != null && table.TryGetValue(key, out var v) ? v : table["other"];

    private static double[] Adjusted(double[] values, params (int index, Func<double, double> change)[] changes)
    {
        var copy = values.ToArray();
        foreach (var (i, f) in changes)
            if (i < copy.Length) copy[i] = Math.Clamp(f(copy[i]), 0, 1);
        return copy;
    }

    // MARK: - Pages

    private static WheelOption Format(string id, string key, string big, string ammo, string title, string detail,
        string? current, Dictionary<string, double[]> traits, string[]? aliases = null, string? requires = null)
    {
        var o = new WheelOption(id, big, ammo, title, detail, traits.TryGetValue(key, out var s) ? s : null);
        if (current != null && (current == key || (aliases?.Contains(current) ?? false)))
            o.Disable("已經是 " + big + " 了", "目前");
        else if (requires != null && !Shell.Has(requires))
            o.Disable(Shell.InstallHint(requires), "未裝備");
        return o;
    }

    private static WheelOption Tool(string id, string ammo, string title, string detail, double[]? stats = null, string? big = null) =>
        new WheelOption(id, big, ammo, title, detail, stats);

    private static WheelOption Needs(WheelOption o, string tool)
    {
        if (o.Enabled && !Shell.Has(tool)) o.Disable(Shell.InstallHint(tool), "未裝備");
        return o;
    }

    private static WheelPage ImageConvert(string? current, int count)
    {
        var t = ImageTraits;
        var pdf = Format("img.to.pdf", "pdf", "PDF", "文件", "PDF", "一張圖一頁，方便傳送和列印", null, t);
        if (count > 1)
        {
            pdf.Title = "合併成 PDF";
            pdf.Detail = count + " 張圖按順序合成一個 PDF";
            pdf.Ammo = "合併";
        }
        return new WheelPage("武器 · 轉換", new WheelOption?[]
        {
            Format("img.to.png", "png", "PNG", "無損", "PNG", "無損畫質，支援透明背景", current, t),
            Format("img.to.jpg", "jpg", "JPG", "通用", "JPG", "到哪都能開啟，體積適中", current, t),
            Format("img.to.heic", "heic", "HEIC", "蘋果", "HEIC", "蘋果相簿格式，小而清晰", current, t),
            Format("img.to.webp", "webp", "WEBP", "網頁", "WebP", "網頁常用，小體積高畫質", current, t),
            pdf,
            Format("img.to.avif", "avif", "AVIF", "極小", "AVIF", "新一代格式，體積最小（不保留透明）", current, t),
            Format("img.to.gif", "gif", "GIF", "256色", "GIF", "老牌格式，只有 256 種顏色", current, t),
            Format("img.to.tiff", "tiff", "TIFF", "印刷", "TIFF", "無損大檔案，適合印刷和存檔", current, t),
        });
    }

    private static WheelPage ImageTools(double[] b) => new("技能 · 工具", new WheelOption?[]
    {
        Tool("img.compress", "壓縮", "壓縮", "肉眼幾乎看不出差別，體積大幅縮小", Adjusted(b, (0, v => v * 0.9), (1, v => v + 0.35))),
        Tool("img.half", "縮小一半", "縮小一半", "長寬各減半，體積約剩四分之一", Adjusted(b, (0, v => v * 0.7), (1, v => v + 0.3))),
        Tool("img.cutout", "去背", "去背", "自動識別主體，去掉背景，匯出透明 PNG", Adjusted(b, (3, _ => 1))),
        Tool("img.ocr", "OCR", "文字辨識", "認出圖裡的文字，直接複製到剪貼簿"),
        Tool("img.mono", "黑白", "黑白", "一鍵變成黑白照片"),
        Tool("img.rotate", "旋轉", "旋轉", "順時針轉 90°"),
        Tool("img.strip", "去隱私", "去隱私", "抹掉 GPS 位置和拍攝資訊，發圖更安心"),
        Tool("img.backdrop", "加背景", "加背景", "加漸層背景、圓角和陰影，截圖秒變精緻"),
    });

    private static WheelPage PdfConvert()
    {
        var t = PdfTraits;
        WheelOption O(string id, string key, string big, string ammo, string title, string detail) =>
            new WheelOption(id, big, ammo, title, detail, t[key]);
        return new WheelPage("武器 · 轉換", new WheelOption?[]
        {
            O("pdf.to.png", "png", "PNG", "每頁一張", "PNG 圖片", "每頁匯出一張清晰圖片"),
            O("pdf.to.jpg", "jpg", "JPG", "每頁一張", "JPG 圖片", "每頁一張，體積更小"),
            O("pdf.to.long", "long", "長圖", "拼接", "長圖", "所有頁面拼成一張長圖，方便分享"),
            O("pdf.to.docx", "docx", "DOCX", "Word", "Word 文件", "提取文字和段落，可以繼續編輯"),
            O("pdf.to.txt", "txt", "TXT", "純文字", "純文字", "只要文字，最乾淨"),
            O("pdf.to.md", "md", "MD", "Markdown", "Markdown", "帶標題層級的純文字，適合餵給 AI"),
            O("pdf.to.rtf", "rtf", "RTF", "富文字", "RTF", "保留基本格式的文字"),
            O("pdf.to.html", "html", "HTML", "網頁", "HTML 網頁", "轉成網頁，瀏覽器直接開啟"),
        });
    }

    private static WheelPage PdfTools(double[] b, int count)
    {
        var merge = Tool("pdf.merge", "合併", "合併", "把拖進來的 " + count + " 個 PDF 按順序合成一個");
        if (count < 2) merge.Disable("同時拖 2 個以上 PDF 才能合併");
        return new WheelPage("技能 · 工具", new WheelOption?[]
        {
            Tool("pdf.compress", "壓縮", "壓縮", "壓縮裡面的圖片，體積變小", Adjusted(b, (0, v => v * 0.92), (2, v => v + 0.3))),
            Tool("pdf.split", "拆頁", "拆頁", "每一頁拆成單獨的 PDF"),
            merge,
            Tool("pdf.rotate", "旋轉", "旋轉", "所有頁面順時針轉 90°"),
            Tool("pdf.mono", "黑白", "黑白", "轉成灰階（頁面會變成圖片），列印省彩墨"),
            Tool("pdf.ocr", "OCR", "文字辨識", "掃描檔也能認出文字，複製到剪貼簿"),
            Strip("pdf.strip", "抹掉作者、製作軟體和建立時間，解除偵查"),
            ZipOption(),
        });
    }

    /// <summary>去隱私: clears the reveal markers (location, device, author…).</summary>
    public static WheelOption Strip(string id, string detail) => Tool(id, "去隱私", "去隱私", detail);

    private static WheelPage VideoConvert(string? current)
    {
        var t = VideoTraits;
        const string ff = "ffmpeg";
        return new WheelPage("武器 · 轉換", new WheelOption?[]
        {
            Format("vid.to.mp4", "mp4", "MP4", "通用", "MP4", "最通用的影片格式", current, t, new[] { "m4v" }, ff),
            Format("vid.to.mov", "mov", "MOV", "蘋果", "MOV", "蘋果剪輯軟體最愛", current, t, null, ff),
            Format("vid.to.hevc", "hevc", "HEVC", "H.265", "HEVC / H.265", "畫質不變，體積約減半", null, t, null, ff),
            Format("vid.to.webm", "webm", "WEBM", "網頁", "WebM", "網頁播放用的開源格式", current, t, null, ff),
            Format("vid.to.gif", "gif", "GIF", "動圖", "GIF 動圖", "轉成動圖（最多取前 30 秒）", current, t, null, ff),
            Format("vid.to.mp3", "mp3", "MP3", "提取音訊", "MP3 音訊", "只要聲音，最通用", null, t, null, ff),
            Format("vid.to.m4a", "m4a", "M4A", "提取音訊", "M4A 音訊", "只要聲音，蘋果裝置友好", null, t, null, ff),
            Format("vid.to.wav", "wav", "WAV", "無損音訊", "WAV 音訊", "只要聲音，無損大檔案", null, t, null, ff),
        });
    }

    private static WheelPage VideoTools(double[] b) => new("技能 · 工具", new WheelOption?[]
    {
        Needs(Tool("vid.compress", "壓縮", "壓縮", "1080P 以內，體積通常小一半以上", Adjusted(b, (0, v => v * 0.88), (1, v => v + 0.3), (2, _ => 1))), "ffmpeg"),
        Needs(Tool("vid.mute", "靜音", "靜音", "去掉聲音，畫面不動"), "ffmpeg"),
        Needs(Tool("vid.720p", "降解析度", "720P", "縮到 720P，傳訊息更快", Adjusted(b, (0, v => Math.Min(v, 0.6)), (1, v => v + 0.25)), "720P"), "ffmpeg"),
        Needs(Tool("vid.cover", "封面", "截封面", "擷取一幀存成 JPG"), "ffmpeg"),
        Needs(Tool("vid.fast", "加速", "2 倍速", "整體加速一倍，聲音同步變快", null, "2×"), "ffmpeg"),
        Needs(Tool("vid.slow", "慢放", "慢放", "0.5 倍速，就像開了子彈時間"), "ffmpeg"),
        Needs(Strip("vid.strip", "抹掉拍攝地點、機型和時間，不重新壓縮"), "ffmpeg"),
        ZipOption(),
    });

    private static WheelPage AudioConvert(string? current)
    {
        var t = AudioTraits;
        const string ff = "ffmpeg";
        return new WheelPage("武器 · 轉換", new WheelOption?[]
        {
            Format("aud.to.mp3", "mp3", "MP3", "通用", "MP3", "最通用的音訊格式", current, t, null, ff),
            Format("aud.to.m4a", "m4a", "M4A", "AAC", "M4A", "蘋果預設，同體積音質更好", current, t, new[] { "aac" }, ff),
            Format("aud.to.wav", "wav", "WAV", "無損", "WAV", "無損大檔案，剪輯常用", current, t, null, ff),
            Format("aud.to.flac", "flac", "FLAC", "無失真壓縮", "FLAC", "無損，體積約為 WAV 的一半", current, t, null, ff),
            Format("aud.to.aiff", "aiff", "AIFF", "蘋果無損", "AIFF", "蘋果的無損格式", current, t, null, ff),
            Format("aud.to.opus", "opus", "OPUS", "極小", "Opus", "體積極小，人聲特別清晰", current, t, null, ff),
            Format("aud.to.alac", "alac", "ALAC", "蘋果無損", "ALAC", "蘋果無失真壓縮，音樂 App 直接播", null, t, null, ff),
            Needs(Tool("aud.to.m4r", "鈴聲", "iPhone 鈴聲", "擷取前 40 秒，做成 iPhone 鈴聲", t["m4r"]), ff),
        });
    }

    private static WheelPage AudioTools(double[] b) => new("技能 · 工具", new WheelOption?[]
    {
        Needs(Tool("aud.compress", "壓縮", "壓縮", "轉成 96 kbps AAC，體積極小", Adjusted(b, (0, v => v * 0.8), (1, _ => 0.95))), "ffmpeg"),
        Needs(Tool("aud.mono", "單聲道", "單聲道", "左右聲道合成一個"), "ffmpeg"),
        Needs(Tool("aud.loud", "音量統一", "音量統一", "音量拉到標準響度，不再忽大忽小"), "ffmpeg"),
        Needs(Tool("aud.fast", "加速", "1.5 倍速", "整體加速，聽 Podcast 更快", null, "1.5×"), "ffmpeg"),
        null,
        null,
        ZipOption(),
        PathOption(),
    });

    private static WheelPage DocConvert(string? current)
    {
        var t = DocTraits;
        var doc = Format("doc.to.doc", "doc", "DOC", "舊版", "舊版 Word", "相容老版本 Office", current, t);
        if (doc.Enabled && !Office.Available) doc.Disable(Office.Hint, "未裝備");
        return new WheelPage("武器 · 轉換", new WheelOption?[]
        {
            Format("doc.to.pdf", "pdf", "PDF", "列印", "PDF", "排版固定，發給誰看都一樣", current, t),
            Format("doc.to.docx", "docx", "DOCX", "Word", "Word 文件", "最常見的可編輯文件", current, t),
            Format("doc.to.txt", "txt", "TXT", "純文字", "純文字", "只要文字，最乾淨", current, t),
            Format("doc.to.md", "md", "MD", "Markdown", "Markdown", "帶標題層級的純文字，適合餵給 AI", current, t),
            Format("doc.to.html", "html", "HTML", "網頁", "HTML 網頁", "瀏覽器直接開啟", current, t),
            Format("doc.to.rtf", "rtf", "RTF", "富文字", "RTF", "保留基本格式的文字", current, t),
            Format("doc.to.odt", "odt", "ODT", "開源", "ODT", "LibreOffice / WPS 的開放格式", current, t),
            doc,
        });
    }

    private static WheelPage DocTools(bool word)
    {
        var lieLow = Strip("doc.strip", "抹掉作者、修改人和公司名");
        if (!word) lieLow.Disable("只有 Word（.docx）文件會帶作者資訊");
        return new WheelPage("技能 · 工具", new WheelOption?[]
        {
            Tool("doc.count", "字數", "字數統計", "數一數一共多少字"),
            null,
            Tool("doc.copy", "複製全文", "複製全文", "把文字全部複製到剪貼簿"),
            null,
            lieLow,
            null,
            ZipOption(),
            PathOption(),
        });
    }

    public static WheelOption ZipOption() => Tool("gen.zip", "壓縮檔", "打包 ZIP", "打包成 .zip 壓縮檔");

    public static WheelOption PathOption() => Tool("gen.path", "複製路徑", "複製路徑", "把檔案路徑複製到剪貼簿");

    private static WheelPage GenericPage(IList<string> files)
    {
        var sha = Tool("gen.sha", "SHA-256", "SHA-256", "算出校驗值並複製");
        if (files.Any(Directory.Exists)) sha.Disable("資料夾算不了校驗值");
        return new WheelPage("通用", new WheelOption?[]
        {
            ZipOption(), null,
            PathOption(), null,
            Tool("gen.name", "複製檔名", "複製檔名", "把檔名複製到剪貼簿"), null,
            sha, null,
        });
    }
}
