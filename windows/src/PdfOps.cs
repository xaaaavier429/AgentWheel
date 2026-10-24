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

    // MARK: - Text and layout

    private sealed class LineInfo
    {
        public List<DocRun> Runs = new();
        public string Text = "";
        public double Size;
        public bool Bullet;
        public bool InTable;
        public UglyToad.PdfPig.Core.PdfRectangle Box;
    }

    /// <summary>One paragraph or picture on a page, with where it sits.</summary>
    private sealed class Item
    {
        public DocPara Para = new();
        public double Top;
        public double Size;
        public List<UglyToad.PdfPig.Core.PdfRectangle> Lines = new();
        public UglyToad.PdfPig.Core.PdfRectangle? ImageBox;
        public int Page;
    }

    private static readonly string[] BulletMarks = { "•", "●", "▪", "■", "◦", "○", "·", "", "", "", "", "–", "-" };

    private static string StripBullet(string text, out bool bullet)
    {
        bullet = false;
        foreach (var mark in BulletMarks)
        {
            if (!text.StartsWith(mark)) continue;
            string rest = text.Substring(mark.Length);
            // "-" and "–" only count with a space after them ("- item"), never in "-5" or "–2020".
            if ((mark == "-" || mark == "–" || mark == "·") && !rest.StartsWith(" ")) return text;
            bullet = true;
            return rest.TrimStart();
        }
        return text;
    }

    private static bool NameHas(string? name, params string[] parts) =>
        name != null && parts.Any(p => name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The look of one letter: weight, slant, size (to half a point) and colour.</summary>
    private static DocRun StyleOf(Pig.Content.Letter l)
    {
        bool bold = l.Font?.IsBold == true || NameHas(l.FontName, "Bold", "Black", "Heavy", "Semibold", "Demi")
                    || l.RenderingMode == UglyToad.PdfPig.Core.TextRenderingMode.FillThenStroke;
        bool italic = l.Font?.IsItalic == true || NameHas(l.FontName, "Italic", "Oblique");
        string? color = null;
        try
        {
            var (r, g, b) = l.Color.ToRGBValues();
            if (Math.Max(r, Math.Max(g, b)) > 0.18)
                color = ((int)Math.Round(r * 255)).ToString("X2") + ((int)Math.Round(g * 255)).ToString("X2") + ((int)Math.Round(b * 255)).ToString("X2");
        }
        catch
        {
            // colour spaces PdfPig can't convert stay black
        }
        return new DocRun("", bold, italic) { Size = Math.Round(l.PointSize * 2) / 2, Color = color };
    }

    /// <summary>Appends text to the paragraph's last run when the style matches, otherwise starts a new run.</summary>
    private static void Append(List<DocRun> runs, string text, DocRun style)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0 && (runs[^1].SameStyle(style) || text.Trim().Length == 0))
        {
            runs[^1].Text += text;
            return;
        }
        runs.Add(new DocRun(text, style.Bold, style.Italic) { Size = style.Size, Color = style.Color });
    }

    private static void AppendRuns(List<DocRun> into, IEnumerable<DocRun> runs)
    {
        foreach (var r in runs) Append(into, r.Text, r);
    }

    private static List<DocRun> RemovePrefix(List<DocRun> runs, int count)
    {
        var result = new List<DocRun>();
        foreach (var r in runs)
        {
            if (count >= r.Text.Length)
            {
                count -= r.Text.Length;
                continue;
            }
            result.Add(new DocRun(r.Text.Substring(count), r.Bold, r.Italic) { Size = r.Size, Color = r.Color });
            count = 0;
        }
        if (result.Count > 0) result[0].Text = result[0].Text.TrimStart();
        return result;
    }

    /// <summary>
    /// The PDF as an editable document: PdfPig finds the text blocks in reading order, lines are regrouped into
    /// paragraphs by spacing, size and indent, and each run keeps its weight, slant, size and colour. Pictures
    /// are placed where they sit on the page, every PDF page starts a new page, and alignment and indents are
    /// measured against the page's text margins.
    /// </summary>
    public static async Task<DocModel> Extract(string path, bool renderMissingImages = true)
    {
        var items = new List<Item>();
        var model = new DocModel();
        var pageSizes = new List<(double w, double h)>();
        try
        {
            using var doc = Pig.PdfDocument.Open(path);
            int pageIndex = 0;
            foreach (var page in doc.GetPages())
            {
                pageSizes.Add((page.Width, page.Height));
                List<Item> pageItems;
                try
                {
                    pageItems = PageItems(page);
                }
                catch
                {
                    pageItems = new List<Item>();
                    string text = page.Text;
                    if (text.Trim().Length > 0)
                    {
                        var item = new Item { Top = page.Height };
                        item.Para.Runs.Add(new DocRun(text));
                        pageItems.Add(item);
                    }
                }
                foreach (var item in pageItems) item.Page = pageIndex;
                items.AddRange(pageItems);
                pageIndex++;
            }
        }
        catch (Exception e) when (e is not FWError)
        {
            throw Friendly(e);
        }

        // Pictures PdfPig can't decode (JPEG 2000, masks…) are cut out of a rendering of their page instead.
        var missing = items.Where(i => i.ImageBox != null && i.Para.Image == null).ToList();
        if (missing.Count > 0 && renderMissingImages && OperatingSystem.IsWindows())
        {
            try
            {
                var winDoc = await LoadForRender(path);
                foreach (var group in missing.GroupBy(i => i.Page))
                {
                    using var rendered = await Render(winDoc, (uint)group.Key, 2);
                    double kx = rendered.Width / pageSizes[group.Key].w, ky = rendered.Height / pageSizes[group.Key].h;
                    foreach (var item in group)
                    {
                        var b = item.ImageBox!.Value;
                        int x = (int)Math.Max(0, b.Left * kx), y = (int)Math.Max(0, (pageSizes[group.Key].h - b.Top) * ky);
                        int w = (int)Math.Min(rendered.Width - x, b.Width * kx), h = (int)Math.Min(rendered.Height - y, b.Height * ky);
                        if (w < 2 || h < 2) continue;
                        using var crop = rendered.Clone();
                        crop.Crop(new MagickGeometry(x, y, w, h));
                        crop.RePage();
                        item.Para.Image = new DocImage(crop.ToByteArray(MagickFormat.Png), "png", b.Width, b.Height);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("pdf: rendering pictures failed: " + e.Message);
            }
        }
        items.RemoveAll(i => i.ImageBox != null && i.Para.Image == null);

        // The text margins: the extent of ordinary body lines across the whole document.
        var allLines = items.SelectMany(i => i.Lines).ToList();
        double pageWidth = pageSizes.Count > 0 ? pageSizes[0].w : 595;
        var body = allLines.Where(l => l.Width > pageWidth * 0.3).ToList();
        if (body.Count == 0) body = allLines;
        double contentLeft = body.Count > 0 ? body.Min(l => l.Left) : 56;
        double contentRight = body.Count > 0 ? body.Max(l => l.Right) : pageWidth - 56;
        double contentWidth = Math.Max(50, contentRight - contentLeft);

        // Headings: noticeably larger than the most common text size.
        double bodySize = items.Where(i => i.Size > 0 && i.Para.Image == null && i.Para.Table == null)
            .GroupBy(i => Math.Round(i.Size))
            .OrderByDescending(g => g.Sum(i => i.Para.Text.Length))
            .Select(g => g.Key)
            .FirstOrDefault();
        if (bodySize <= 0) bodySize = 12;

        int lastPage = -1;
        foreach (var item in items)
        {
            var p = item.Para;
            if (item.Page != lastPage)
            {
                p.PageBreak = lastPage >= 0;
                lastPage = item.Page;
            }
            if (p.Table is DocTable table)
            {
                double l = item.Lines.Min(x => x.Left), r = item.Lines.Max(x => x.Right);
                double tolerance = Math.Max(6, contentWidth * 0.04);
                if (Math.Abs((l - contentLeft) - (contentRight - r)) < tolerance * 2 && l - contentLeft > tolerance * 2) table.Align = DocAlign.Center;
                else if (l - contentLeft > 8) table.Indent = Math.Round(l - contentLeft);
                model.Paras.Add(p);
                continue;
            }
            if (p.Image != null)
            {
                var b = item.ImageBox!.Value;
                double mid = (b.Left + b.Right) / 2;
                if (Math.Abs(mid - (contentLeft + contentRight) / 2) < contentWidth * 0.08 && b.Width < contentWidth * 0.9) p.Align = DocAlign.Center;
                else if (b.Left - contentLeft > 8) p.Indent = Math.Min(b.Left - contentLeft, contentWidth * 0.6);
                model.Paras.Add(p);
                continue;
            }
            foreach (var r in p.Runs) r.Text = TextFix.Radicals(r.Text);
            if (p.Runs.Count > 0)
            {
                p.Runs[0].Text = p.Runs[0].Text.TrimStart();
                p.Runs[^1].Text = p.Runs[^1].Text.TrimEnd();
            }
            p.Runs.RemoveAll(r => r.Text.Length == 0);
            string text = p.Text;
            if (text.Trim().Length == 0) continue;
            if (!p.Bullet && text.Length <= 120)
            {
                bool allBold = p.Runs.All(r => r.Bold || r.Text.Trim().Length == 0);
                if (item.Size >= bodySize * 1.55) p.Heading = 1;
                else if (item.Size >= bodySize * 1.25) p.Heading = 2;
                else if (item.Size >= bodySize * 1.1 || (allBold && text.Length <= 40 && item.Lines.Count == 1)) p.Heading = 3;
            }

            // Alignment and indent, measured against the text margins.
            double tol = Math.Max(4, contentWidth * 0.03);
            double minLeft = item.Lines.Min(l => l.Left), maxRight = item.Lines.Max(l => l.Right);
            bool centered = item.Lines.All(l => Math.Abs((l.Left - contentLeft) - (contentRight - l.Right)) < tol * 2) && minLeft - contentLeft > tol * 2;
            if (centered) p.Align = DocAlign.Center;
            else if (contentRight - maxRight < tol && minLeft - contentLeft > contentWidth * 0.35) p.Align = DocAlign.Right;
            else if (minLeft - contentLeft > 8) p.Indent = Math.Round(Math.Min(minLeft - contentLeft, contentWidth * 0.6));
            model.Paras.Add(p);
        }

        if (pageSizes.Count > 0)
        {
            var first = pageSizes[0];
            var firstLines = items.Where(i => i.Page == items.FirstOrDefault()?.Page).SelectMany(i => i.Lines).ToList();
            double top = firstLines.Count > 0 ? first.h - firstLines.Max(l => l.Top) : 56;
            model.PageWidth = Math.Round(first.w, 1);
            model.PageHeight = Math.Round(first.h, 1);
            model.MarginLeft = Math.Clamp(Math.Round(contentLeft), 18, 108);
            model.MarginRight = Math.Clamp(Math.Round(first.w - contentRight), 18, 108);
            model.MarginTop = Math.Clamp(Math.Round(top), 18, 90);
            model.MarginBottom = Math.Clamp(Math.Round(model.MarginTop * 0.9), 18, 72);
        }
        if (model.Paras.Count == 0) throw new FWError("這個 PDF 沒有文字層，試試「技能 → 文字辨識」");
        return model;
    }

    private static List<Item> PageItems(Pig.Content.Page page)
    {
        var result = new List<Item>();
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count > 0)
        {
            var segmented = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = ReadingOrder(segmented.ToList(), b => b.BoundingBox);
            var marks = new List<UglyToad.PdfPig.Core.PdfRectangle>();
            var blocks = ordered.Select(b => (box: b.BoundingBox, lines: BlockLines(b, marks))).ToList();
            // A bullet glyph often ends up as its own little block; it belongs to the line just right of it.
            var allLines = blocks.SelectMany(b => b.lines).ToList();
            foreach (var mark in marks)
            {
                double mid = (mark.Top + mark.Bottom) / 2;
                var owner = allLines.Where(l => l.Box.Left >= mark.Left && l.Box.Left - mark.Right < 40 && mid <= l.Box.Top + 2 && mid >= l.Box.Bottom - 2)
                    .OrderBy(l => l.Box.Left - mark.Right).FirstOrDefault();
                if (owner != null) owner.Bullet = true;
            }
            var tables = Tables(allLines);
            foreach (var (box, lines) in blocks) result.AddRange(Paragraphs(box, lines.Where(l => !l.InTable).ToList()));
            foreach (var table in tables)
            {
                int at = result.FindIndex(i => i.Top <= table.Top + 1);
                if (at < 0) result.Add(table);
                else result.Insert(at, table);
            }
        }

        // Pictures, slotted in among the paragraphs by height on the page.
        foreach (var image in page.GetImages().OrderByDescending(i => i.Bounds.Top))
        {
            var b = image.Bounds;
            if (b.Width < 12 || b.Height < 12) continue;
            // A picture behind the whole page with text on it is a background, not content.
            if (result.Count > 0 && b.Width * b.Height > page.Width * page.Height * 0.8) continue;
            var item = new Item { Top = b.Top, ImageBox = b };
            try
            {
                if (image.TryGetPng(out var png)) item.Para.Image = new DocImage(png, "png", b.Width, b.Height);
                else
                {
                    var raw = image.RawBytes;
                    if (raw.Length > 4 && raw[0] == 0xFF && raw[1] == 0xD8 && !IsCmyk(image))
                        item.Para.Image = new DocImage(raw.ToArray(), "jpeg", b.Width, b.Height);
                }
            }
            catch
            {
                // left for the renderer
            }
            int at = result.FindIndex(i => i.ImageBox == null && i.Top <= b.Top + 1);
            if (at < 0) result.Add(item);
            else result.Insert(at, item);
        }
        return result;
    }

    /// <summary>
    /// Top to bottom, except where blocks sit side by side in columns: between two full-width blocks, the left
    /// column is read before the right one.
    /// </summary>
    private static List<T> ReadingOrder<T>(List<T> blocks, Func<T, UglyToad.PdfPig.Core.PdfRectangle> box)
    {
        if (blocks.Count < 2) return blocks;
        var sorted = blocks.OrderByDescending(b => box(b).Top).ToList();
        double mid = (sorted.Min(b => box(b).Left) + sorted.Max(b => box(b).Right)) / 2;
        bool Spans(UglyToad.PdfPig.Core.PdfRectangle r) => r.Left < mid - 10 && r.Right > mid + 10;
        bool Overlap(UglyToad.PdfPig.Core.PdfRectangle a, UglyToad.PdfPig.Core.PdfRectangle b) => a.Bottom < b.Top && b.Bottom < a.Top;
        var result = new List<T>();
        var band = new List<T>();
        void Flush()
        {
            bool columns = band.Any(a => band.Any(b => box(a).Right <= mid + 10 && box(b).Left >= mid - 10 && Overlap(box(a), box(b))));
            if (columns) band = band.OrderBy(b => box(b).Left < mid - 10 ? 0 : 1).ThenByDescending(b => box(b).Top).ToList();
            result.AddRange(band);
            band.Clear();
        }
        foreach (var b in sorted)
        {
            if (Spans(box(b)))
            {
                Flush();
                result.Add(b);
            }
            else
            {
                band.Add(b);
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Tables: two or more consecutive rows that each hold the same number (2+) of separate, short pieces of text
    /// lined up under one another.
    /// </summary>
    private static List<Item> Tables(List<LineInfo> lines)
    {
        var result = new List<Item>();
        // Rows: lines sharing a baseline.
        var rows = new List<List<LineInfo>>();
        foreach (var line in lines.OrderByDescending(l => (l.Box.Top + l.Box.Bottom) / 2))
        {
            double center = (line.Box.Top + line.Box.Bottom) / 2;
            var row = rows.LastOrDefault();
            if (row != null)
            {
                var first = row[0];
                double firstCenter = (first.Box.Top + first.Box.Bottom) / 2;
                if (Math.Abs(center - firstCenter) < Math.Max(first.Box.Height, line.Box.Height) * 0.5)
                {
                    row.Add(line);
                    continue;
                }
            }
            rows.Add(new List<LineInfo> { line });
        }
        foreach (var row in rows) row.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));

        bool Cells(List<LineInfo> row)
        {
            if (row.Count < 2) return false;
            for (int i = 1; i < row.Count; i++)
                if (row[i].Box.Left - row[i - 1].Box.Right < Math.Max(8, Math.Max(row[i].Size, row[i - 1].Size) * 1.2)) return false;
            return true;
        }

        int r = 0;
        while (r < rows.Count)
        {
            if (!Cells(rows[r]))
            {
                r++;
                continue;
            }
            int n = rows[r].Count;
            var columns = rows[r].Select(l => (left: l.Box.Left, right: l.Box.Right)).ToList();
            int end = r + 1;
            while (end < rows.Count && Cells(rows[end]) && rows[end].Count == n)
            {
                var prev = rows[end - 1];
                double pitch = ((prev[0].Box.Top + prev[0].Box.Bottom) - (rows[end][0].Box.Top + rows[end][0].Box.Bottom)) / 2;
                if (pitch > Math.Max(prev[0].Box.Height, 6) * 4) break;
                bool aligned = true;
                for (int k = 0; k < n && aligned; k++)
                {
                    var c = rows[end][k].Box;
                    aligned = c.Left < columns[k].right + 4 && c.Right > columns[k].left - 4;
                }
                if (!aligned) break;
                for (int k = 0; k < n; k++)
                    columns[k] = (Math.Min(columns[k].left, rows[end][k].Box.Left), Math.Max(columns[k].right, rows[end][k].Box.Right));
                end++;
            }
            var group = rows.GetRange(r, end - r);
            var cells = group.SelectMany(g => g).ToList();
            double span = columns[^1].right - columns[0].left;
            // Real tables hold short entries; two columns of running text are a page layout, not a table.
            bool shortCells = cells.Average(c => c.Text.Length) < 40 && cells.Max(c => c.Box.Width) < Math.Max(span * 0.6, 120);
            if (group.Count >= 2 && shortCells)
            {
                var table = new DocTable();
                // Each column runs to where the next one starts; cells get a little room for their padding.
                double em = Math.Max(8, cells.Average(c => c.Size));
                for (int k = 0; k < n; k++)
                    table.Widths.Add(Math.Round(Math.Max(em * 3, k + 1 < n ? columns[k + 1].left - columns[k].left : columns[k].right - columns[k].left + em * 1.5) + 8));
                foreach (var row in group)
                {
                    table.Rows.Add(row.Select(l => l.Runs.Select(x => new DocRun(TextFix.Radicals(x.Text), x.Bold, x.Italic) { Size = x.Size, Color = x.Color }).ToList()).ToList());
                    foreach (var l in row) l.InTable = true;
                }
                var item = new Item { Top = group[0].Max(l => l.Box.Top), Size = group[0][0].Size };
                item.Para.Table = table;
                item.Lines.AddRange(cells.Select(c => c.Box));
                result.Add(item);
                r = end;
            }
            else
            {
                r++;
            }
        }
        return result;
    }

    private static bool IsCmyk(Pig.Content.IPdfImage image)
    {
        try
        {
            return image.ColorSpaceDetails?.NumberOfColorComponents == 4;
        }
        catch
        {
            return false;
        }
    }

    private static List<LineInfo> BlockLines(UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock block, List<UglyToad.PdfPig.Core.PdfRectangle> marks)
    {
        var lines = new List<LineInfo>();
        foreach (var line in block.TextLines)
        {
            var lineWords = line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.BoundingBox.Left).ToList();
            if (lineWords.Count == 0) continue;
            var runs = new List<DocRun>();
            string text = "";
            var sizes = new List<double>();
            double lastRight = 0;
            foreach (var word in lineWords)
            {
                var letters = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                if (letters.Count == 0) continue;
                var first = StyleOf(letters[0]);
                // Within a line, a visible gap between two words is a space, whatever the script.
                double gap = word.BoundingBox.Left - lastRight;
                if (text.Length > 0 && gap > Math.Max(1, first.Size * 0.15))
                {
                    Append(runs, " ", first);
                    text += " ";
                }
                foreach (var letter in letters)
                {
                    var style = StyleOf(letter);
                    if (style.Size > 0) sizes.Add(style.Size);
                    Append(runs, letter.Value, style);
                    text += letter.Value;
                }
                lastRight = word.BoundingBox.Right;
            }
            string rest = StripBullet(text, out bool bullet);
            if (bullet) runs = RemovePrefix(runs, text.Length - rest.Length);
            if (rest.Trim().Length == 0)
            {
                if (bullet) marks.Add(line.BoundingBox);
                continue;
            }
            lines.Add(new LineInfo
            {
                Runs = runs,
                Text = rest,
                Size = sizes.Count > 0 ? sizes.OrderBy(v => v).ElementAt(sizes.Count / 2) : 0,
                Bullet = bullet,
                Box = line.BoundingBox,
            });
        }
        return lines;
    }

    private static List<Item> Paragraphs(UglyToad.PdfPig.Core.PdfRectangle box, List<LineInfo> lines)
    {
        // The usual gap between lines of this block; a clearly bigger one starts a new paragraph.
        var gaps = lines.Zip(lines.Skip(1), (a, b) => a.Box.Bottom - b.Box.Top).Where(g => g > -2).OrderBy(g => g).ToList();
        double typicalGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;

        var result = new List<Item>();
        Item? current = null;
        LineInfo? previous = null;
        foreach (var line in lines)
        {
            bool split = current == null || previous == null || line.Bullet;
            if (!split)
            {
                double gap = previous!.Box.Bottom - line.Box.Top;
                double limit = Math.Max(Math.Max(line.Size, previous.Size) * 0.85, typicalGap * 1.6 + 1);
                // A line that stops well short of the block's right edge usually ends its paragraph.
                bool previousShort = box.Width > 0 && previous.Box.Right < box.Right - Math.Max(box.Width * 0.18, previous.Size * 3);
                bool boldNow = line.Runs.All(r => r.Bold), boldBefore = previous.Runs.All(r => r.Bold);
                double em = Math.Max(4, Math.Max(line.Size, previous.Size));
                if (Math.Abs(line.Size - previous.Size) > Math.Max(line.Size, previous.Size) * 0.12) split = true;
                // Indented further than the line above: a new paragraph (first-line indent) or a nested item.
                else if (line.Box.Left - previous.Box.Left > em) split = true;
                // Back out to the left after the paragraph's second line: the indented block has ended.
                else if (previous.Box.Left - line.Box.Left > em && current!.Lines.Count > 1) split = true;
                else if (gap > limit) split = true;
                else if (previousShort) split = true;
                else if (boldNow != boldBefore && (boldNow || current!.Para.Text.Length < 60)) split = true;
            }
            if (split)
            {
                current = new Item { Top = line.Box.Top, Size = line.Size };
                current.Para.Bullet = line.Bullet;
                AppendRuns(current.Para.Runs, line.Runs);
                current.Lines.Add(line.Box);
                result.Add(current);
            }
            else
            {
                if (!TextFix.JoinsTight(current!.Para.Text, line.Text)) Append(current.Para.Runs, " ", current.Para.Runs[^1]);
                AppendRuns(current.Para.Runs, line.Runs);
                current.Lines.Add(line.Box);
            }
            previous = line;
        }
        return result;
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
        var model = await Extract(path);
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
