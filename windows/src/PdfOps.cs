using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Windows.Storage;
using Windows.Storage.Streams;
using Pig = UglyToad.PdfPig;
using WinPdf = Windows.Data.Pdf;

namespace AgentWheel;

/// <summary>
/// PDF: pages are rendered by the PDF engine built into Windows, edited with PDFsharp
/// and read as text with PdfPig.
/// </summary>
public static class PdfOps
{
    private static FWError Friendly(Exception e) =>
        e.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || e.Message.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0
            ? new FWError("這個 PDF 有密碼保護")
            : new FWError("打不開這個 PDF：" + Fmt.Trunc(e.Message, 80));

    public static PdfDocument OpenForEdit(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (Exception e)
        {
            throw Friendly(e);
        }
    }

    private static PdfDocument OpenForImport(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (Exception e)
        {
            throw Friendly(e);
        }
    }

    // MARK: - Rendering

    private static async Task<WinPdf.PdfDocument> LoadForRender(string path)
    {
        WinPdf.PdfDocument doc;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception)
        {
            throw new FWError("打不開這個 PDF（可能有密碼保護）");
        }
        if (doc.PageCount == 0) throw new FWError("PDF 裡沒有頁面");
        return doc;
    }

    /// <summary>Renders one page; <paramref name="scale"/> is relative to 72 dpi (2 = 144 dpi).</summary>
    private static async Task<MagickImage> Render(WinPdf.PdfDocument doc, uint index, double scale)
    {
        using var page = doc.GetPage(index);
        // page.Size is in 1/96-inch units; points are 1/72 inch.
        double w = page.Size.Width * 0.75 * scale, h = page.Size.Height * 0.75 * scale;
        const double maxPixels = 60_000_000;
        if (w * h > maxPixels)
        {
            double k = Math.Sqrt(maxPixels / (w * h));
            w *= k;
            h *= k;
        }
        var options = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(w)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(h)),
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);
        var bytes = new MemoryStream();
        using (var read = stream.AsStreamForRead()) await read.CopyToAsync(bytes);
        bytes.Position = 0;
        var image = new MagickImage(bytes);
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        return image;
    }

    public static async Task<OpResult> ToImages(string path, string fmt, ProgressFn progress)
    {
        var doc = await LoadForRender(path);
        uint count = doc.PageCount;
        int? quality = fmt == "jpg" ? 90 : null;
        if (count == 1)
        {
            using var image = await Render(doc, 0, 2);
            string output = Out.Target(path, null, fmt);
            await ImageOps.Write(image, output, fmt, quality);
            return new OpResult(output);
        }
        string folder = Out.Folder(path, fmt.ToUpperInvariant());
        string name = Path.GetFileNameWithoutExtension(path);
        int digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        for (uint i = 0; i < count; i++)
        {
            using var image = await Render(doc, i, 2);
            string number = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
            await ImageOps.Write(image, Path.Combine(folder, name + "-" + number + "." + fmt), fmt, quality);
            progress((i + 1) / (double)count);
        }
        return new OpResult(folder) { Note = count + " 頁 → 資料夾" };
    }

    /// <summary>Every page stacked into one tall JPG (split into parts past JPEG's height limit).</summary>
    public static async Task<OpResult> LongImage(string path, ProgressFn progress)
    {
        var doc = await LoadForRender(path);
        int count = (int)Math.Min(doc.PageCount, 120);
        const int width = 1242;
        const int gap = 16;
        var rendered = new List<MagickImage>();
        try
        {
            for (uint i = 0; i < count; i++)
            {
                double pageWidthPoints;
                using (var page = doc.GetPage(i)) pageWidthPoints = page.Size.Width * 0.75;
                var image = await Render(doc, i, width / Math.Max(1, pageWidthPoints));
                if (image.Width != width) image.Resize(new MagickGeometry(width, 0));
                rendered.Add(image);
                progress((i + 1) / (double)count * 0.8);
            }
            if (rendered.Count == 0) throw new FWError("頁面渲染失敗");

            var parts = new List<List<MagickImage>> { new() };
            int height = 0;
            foreach (var image in rendered)
            {
                if (height + image.Height > 60_000 && parts[^1].Count > 0)
                {
                    parts.Add(new List<MagickImage>());
                    height = 0;
                }
                parts[^1].Add(image);
                height += image.Height + gap;
            }

            var outputs = new List<string>();
            for (int n = 0; n < parts.Count; n++)
            {
                var part = parts[n];
                int total = part.Sum(im => im.Height) + gap * (part.Count - 1);
                using var canvas = new MagickImage(new MagickColor("#E6E6E6"), width, total);
                int y = 0;
                foreach (var image in part)
                {
                    canvas.Composite(image, 0, y, CompositeOperator.Over);
                    y += image.Height + gap;
                }
                string suffix = parts.Count > 1 ? "長圖 " + (n + 1) : "長圖";
                string output = Out.Target(path, suffix, "jpg");
                await ImageOps.Write(canvas, output, "jpg", 88);
                outputs.Add(output);
            }
            progress(1);
            uint skipped = doc.PageCount - (uint)count;
            return new OpResult(outputs.ToArray())
            {
                Note = skipped > 0 ? "只拼了前 " + count + " 頁" : count + " 頁拼成" + (outputs.Count > 1 ? " " + outputs.Count + " 張" : "一張"),
            };
        }
        finally
        {
            foreach (var image in rendered) image.Dispose();
        }
    }

    public static async Task<OpResult> Mono(string path, ProgressFn progress)
    {
        var doc = await LoadForRender(path);
        var output = new PdfDocument();
        var keep = new List<MemoryStream>();
        try
        {
            for (uint i = 0; i < doc.PageCount; i++)
            {
                double wPt, hPt;
                using (var page = doc.GetPage(i))
                {
                    wPt = page.Size.Width * 0.75;
                    hPt = page.Size.Height * 0.75;
                }
                using var image = await Render(doc, i, 2);
                image.Grayscale();
                AddImagePage(output, image, keep, wPt, hPt, 85);
                progress((i + 1) / (double)doc.PageCount);
            }
            string target = Out.Target(path, "黑白", "pdf");
            output.Save(target);
            return new OpResult(target);
        }
        finally
        {
            output.Dispose();
            foreach (var s in keep) s.Dispose();
        }
    }

    public static async Task<OpResult> Ocr(string path, ProgressFn progress)
    {
        var doc = await LoadForRender(path);
        var parts = new List<string>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var image = await Render(doc, i, 2);
            parts.Add(await AgentWheel.Ocr.Recognize(image));
            progress((i + 1) / (double)doc.PageCount);
        }
        string text = string.Join("\n\n", parts).Trim();
        if (text.Length == 0) throw new FWError("沒認出文字");
        string output = Out.Target(path, "文字", "txt");
        await File.WriteAllTextAsync(output, text);
        return new OpResult(output) { Note = "認出 " + Fmt.Count(text.Length) + " 字，已複製", Clip = text };
    }

    // MARK: - Building PDFs from images

    /// <summary>Adds a page showing the image. Opaque images are embedded as JPEG so the PDF stays small.</summary>
    public static void AddImagePage(PdfDocument doc, IMagickImage<byte> image, List<MemoryStream> keep,
        double? widthPoints = null, double? heightPoints = null, int jpegQuality = 90)
    {
        using var copy = image.Clone();
        var bytes = new MemoryStream();
        if (ImageOps.HasAlpha(copy))
        {
            copy.Write(bytes, MagickFormat.Png);
        }
        else
        {
            copy.ColorSpace = ColorSpace.sRGB;
            copy.Quality = jpegQuality;
            copy.Write(bytes, MagickFormat.Jpeg);
        }
        bytes.Position = 0;
        keep.Add(bytes);
        var picture = XImage.FromStream(bytes);
        // Keep full resolution but give the page a sensible physical size (long edge ≈ A4).
        double k = 842.0 / Math.Max(image.Width, image.Height);
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(widthPoints ?? image.Width * k);
        page.Height = XUnit.FromPoint(heightPoints ?? image.Height * k);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
    }

    public static void ImagesToPdf(IEnumerable<IMagickImage<byte>> images, string output)
    {
        var keep = new List<MemoryStream>();
        using var doc = new PdfDocument();
        doc.Info.Creator = "特戰輪盤";
        try
        {
            foreach (var image in images) AddImagePage(doc, image, keep);
            doc.Save(output);
        }
        finally
        {
            foreach (var s in keep) s.Dispose();
        }
    }

    // MARK: - Editing

    public static OpResult Compress(string path)
    {
        string output = Out.Target(path, "壓縮", "pdf");
        using (var doc = OpenForEdit(path))
        {
            // Re-encode large JPEG images at screen resolution, like "optimize for screen".
            var seen = new HashSet<PdfDictionary>();
            foreach (PdfPage page in doc.Pages)
            {
                var resources = page.Elements.GetDictionary("/Resources");
                var xobjects = resources?.Elements.GetDictionary("/XObject");
                if (xobjects == null) continue;
                foreach (var item in xobjects.Elements.Values)
                {
                    if (item is not PdfReference reference || reference.Value is not PdfDictionary picture || !seen.Add(picture)) continue;
                    if (picture.Elements.GetName("/Subtype") != "/Image" || picture.Stream == null) continue;
                    var filter = picture.Elements["/Filter"];
                    string name = filter is PdfName n ? n.Value : filter is PdfArray a && a.Elements.Count == 1 && a.Elements[0] is PdfName n2 ? n2.Value : "";
                    if (name != "/DCTDecode" || picture.Elements.GetName("/ColorSpace") != "/DeviceRGB") continue;
                    byte[] original = picture.Stream.Value;
                    try
                    {
                        using var image = new MagickImage(original);
                        if (Math.Max(image.Width, image.Height) > 1600) image.Resize(new MagickGeometry(1600, 1600));
                        image.Strip();
                        image.Quality = 70;
                        byte[] smaller = image.ToByteArray(MagickFormat.Jpeg);
                        if (smaller.Length >= original.Length) continue;
                        picture.Stream.Value = smaller;
                        picture.Elements.SetInteger("/Width", image.Width);
                        picture.Elements.SetInteger("/Height", image.Height);
                    }
                    catch
                    {
                        // leave images we can't decode untouched
                    }
                }
            }
            doc.Options.CompressContentStreams = true;
            doc.Options.NoCompression = false;
            doc.Save(output);
        }
        try
        {
            return ImageOps.ShrinkResult(path, output);
        }
        catch (FWError)
        {
            throw new FWError("這個 PDF 已經很精簡了");
        }
    }

    public static OpResult Split(string path)
    {
        using var input = OpenForImport(path);
        if (input.PageCount < 2) throw new FWError("只有 1 頁，不用拆");
        string folder = Out.Folder(path, "拆分");
        string name = Path.GetFileNameWithoutExtension(path);
        for (int i = 0; i < input.PageCount; i++)
        {
            using var single = new PdfDocument();
            single.AddPage(input.Pages[i]);
            single.Save(Path.Combine(folder, name + "-" + (i + 1) + ".pdf"));
        }
        return new OpResult(folder) { Note = "拆成 " + input.PageCount + " 個檔案" };
    }

    /// <summary>PDFs and images, in drag order, into one document.</summary>
    public static string Merge(IList<string> files)
    {
        var inputs = new List<PdfDocument>();
        var keep = new List<MemoryStream>();
        using var merged = new PdfDocument();
        try
        {
            foreach (var file in files)
            {
                switch (Kinds.Of(file))
                {
                    case FileKind.pdf:
                    {
                        var input = OpenForImport(file);
                        inputs.Add(input);
                        foreach (PdfPage page in input.Pages) merged.AddPage(page);
                        break;
                    }
                    case FileKind.image:
                    {
                        using var image = ImageOps.Load(file);
                        AddImagePage(merged, image, keep);
                        break;
                    }
                }
            }
            if (merged.PageCount == 0 || files.Count == 0) throw new FWError("沒有可以合併的頁面");
            string output = Out.Target(files[0], "合併", "pdf");
            merged.Save(output);
            return output;
        }
        finally
        {
            foreach (var input in inputs) input.Dispose();
            foreach (var s in keep) s.Dispose();
        }
    }

    public static OpResult Rotate(string path)
    {
        string output = Out.Target(path, "旋轉", "pdf");
        using (var doc = OpenForEdit(path))
        {
            foreach (PdfPage page in doc.Pages) page.Rotate = (page.Rotate + 90) % 360;
            doc.Save(output);
        }
        return new OpResult(output);
    }

    // MARK: - Text and layout (see PdfLayout)

    /// <summary>Cuts regions out of pages rendered by the PDF engine built into Windows (2×, one page cached).</summary>
    public static RegionRenderer? PageRenderer(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        WinPdf.PdfDocument? doc = null;
        int cachedPage = -1;
        MagickImage? cached = null;
        return async (page, rect, pageWidth, pageHeight) =>
        {
            try
            {
                doc ??= await LoadForRender(path);
                if (cachedPage != page)
                {
                    cached?.Dispose();
                    cached = await Render(doc, (uint)page, 2);
                    cachedPage = page;
                }
                double kx = cached!.Width / pageWidth, ky = cached.Height / pageHeight;
                int x = (int)Math.Max(0, Math.Floor(rect.Left * kx)), y = (int)Math.Max(0, Math.Floor((pageHeight - rect.Top) * ky));
                int w = (int)Math.Min(cached.Width - x, Math.Ceiling(rect.Width * kx)), h = (int)Math.Min(cached.Height - y, Math.Ceiling(rect.Height * ky));
                if (w < 2 || h < 2) return null;
                using var crop = cached.Clone();
                crop.Crop(new MagickGeometry(x, y, w, h));
                crop.RePage();
                bool photo = w * h > 600_000;
                byte[] bytes = photo ? crop.ToByteArray(MagickFormat.Jpeg) : crop.ToByteArray(MagickFormat.Png);
                return new DocImage(bytes, photo ? "jpeg" : "png", rect.Width, rect.Height);
            }
            catch (Exception e)
            {
                Log.Write("pdf: rendering a region failed: " + e.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// PDF → Word, RTF, ODT or HTML goes through Word when it is installed: Word's own PDF import keeps tables
    /// and the layout best. Otherwise, and for TXT / Markdown, the built-in extractor above is used.
    /// </summary>
    public static async Task<OpResult> ToText(string path, string fmt)
    {
        string output = Out.Target(path, null, fmt);
        if (Office.HasWord && fmt is "docx" or "rtf" or "odt" or "html")
        {
            try
            {
                await Office.Convert(path, output, fmt);
                if (File.Exists(output)) return new OpResult(output);
            }
            catch (Exception e)
            {
                Log.Write("pdf: Word conversion failed, using the built-in one: " + e.Message);
                Out.TryDelete(output);
            }
        }
        var model = await PdfLayout.Extract(path, PageRenderer(path));
        if (fmt is "txt" or "md" && model.PlainText.Trim().Length == 0)
            throw new FWError("這個 PDF 沒有文字層，試試「技能 → 文字辨識」");
        await DocWriters.Write(model, output, fmt);
        return new OpResult(output);
    }
}

public static class TextFix
{
    /// <summary>
    /// Chinese PDFs often extract as radical code points (⽂ ⻓ ⻔ …) because the font shares one glyph between a
    /// radical and the ordinary character. Kangxi radicals fold via NFKC; the supplement radicals are mapped by hand.
    /// </summary>
    private static readonly Dictionary<int, char> Supplement = new()
    {
        [0x2E9F] = '母', [0x2EA0] = '民', [0x2EC4] = '西', [0x2EC5] = '見', [0x2EC6] = '角', [0x2EC8] = '訁', [0x2EC9] = '貝',
        [0x2ECB] = '車', [0x2ECE] = '辶', [0x2ED0] = '釒', [0x2ED3] = '長', [0x2ED4] = '門', [0x2ED7] = '雨', [0x2ED8] = '青',
        [0x2EDA] = '頁', [0x2EDB] = '風', [0x2EDC] = '飛', [0x2EDD] = '食', [0x2EE0] = '飠', [0x2EE2] = '馬', [0x2EE4] = '鬼',
        [0x2EE5] = '魚', [0x2EE6] = '鳥', [0x2EE7] = '滷', [0x2EE8] = '麥', [0x2EE9] = '黃', [0x2EEA] = '黽', [0x2EEC] = '齊',
        [0x2EEE] = '齒', [0x2EF0] = '龍', [0x2EF3] = '龜',
    };

    public static string Radicals(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch >= 0x2F00 && ch <= 0x2FD5)
            {
                string folded = ch.ToString().Normalize(NormalizationForm.FormKC);
                sb.Append(folded.Length == 1 ? folded : ch.ToString());
            }
            else if (ch >= 0x2E80 && ch <= 0x2EF3 && Supplement.TryGetValue(ch, out var fixedChar))
            {
                sb.Append(fixedChar);
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    public static bool IsCJK(int c) =>
        (c >= 0x2E80 && c <= 0x2FDF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF)
        || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x20000 && c <= 0x2FA1F);

    /// <summary>Chinese text joins across a break without a space; Latin text needs one.</summary>
    public static bool JoinsTight(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0) return true;
        char a = before[^1], b = after[0];
        if (IsCJK(a) && IsCJK(b)) return true;
        // Chinese punctuation never takes a space on either side; Latin closing punctuation hugs the word before it.
        if ((IsCJK(a) && char.IsPunctuation(a)) || (IsCJK(b) && char.IsPunctuation(b))) return true;
        return b is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '%';
    }

    public static string JoinWords(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0 && !JoinsTight(sb.ToString(), w)) sb.Append(' ');
            sb.Append(w);
        }
        return sb.ToString();
    }
}
