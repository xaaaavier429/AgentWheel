using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Pig = UglyToad.PdfPig;

namespace AgentWheel;

/// <summary>Renders part of a page (PDF points, origin bottom-left) as a picture; null when it can't.</summary>
public delegate Task<DocImage?> RegionRenderer(int page, PdfRectangle rect, double pageWidth, double pageHeight);

/// <summary>
/// PDF → editable document that keeps the layout. Per page:
/// words → line pieces (split at wide gaps, so columns and table cells stay apart) → figures (clusters of
/// vector drawings and pictures, rendered as one image with their labels) → tables → the column gutter →
/// reading order (left column before right between full-width parts) → paragraphs.
/// </summary>
public static class PdfLayout
{
    /// <summary>Receives layout decisions while debugging (tests only).</summary>
    public static Action<string>? Trace;

    private sealed class Seg
    {
        public List<DocRun> Runs = new();
        public string Text = "";
        public double Size;
        public double Baseline;
        public bool Bullet;
        public bool Used;
        public int Letters;
        public int MathLetters;
        public PdfRectangle Box;
        public int Column = -1;
        /// <summary>A figure or table standing in the reading order in place of text.</summary>
        public Item? Block;
    }

    private sealed class Item
    {
        public DocPara Para = new();
        public double Top;
        public double Size;
        public List<PdfRectangle> Lines = new();
        public PdfRectangle? Box;
        public int Page;
        public int Column = -1;
        public bool TwoColumns;
        public double Pitch;
        public List<double> Baselines = new();
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
        bool bold = l.Font?.IsBold == true || NameHas(l.FontName, "Bold", "Black", "Heavy", "Semibold", "Demi", ".B", "-B")
                    || l.RenderingMode == TextRenderingMode.FillThenStroke;
        bool italic = l.Font?.IsItalic == true || NameHas(l.FontName, "Italic", "Oblique", ".I", "-I");
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
        return new DocRun("", bold, italic) { Size = Math.Round(l.PointSize * 2) / 2, Color = color, Font = FontFamily(l.FontName) };
    }

    private static readonly Dictionary<string, string?> FamilyCache = new();

    /// <summary>
    /// A font family Word will have for a PDF font name: "ABCDEF+NimbusRomNo9L-Medi" → "Times New Roman",
    /// "Calibri-Bold" → "Calibri". Chinese and maths fonts give null (the document default handles them).
    /// </summary>
    public static string? FontFamily(string? pdfName)
    {
        if (string.IsNullOrEmpty(pdfName)) return null;
        lock (FamilyCache)
        {
            if (FamilyCache.TryGetValue(pdfName, out var cached)) return cached;
            string name = pdfName;
            int plus = name.IndexOf('+');
            if (plus == 6) name = name.Substring(7);
            string lower = name.ToLowerInvariant();
            string? family;
            if (new[] { "ming", "song", "hei", "kai", "pingfang", "jhenghei", "yahei", "cjk", "sourcehan", "gothic", "mincho", "wenquanyi", "simsun", "dfkai", "biaukai", "hiragino", "noto sans tc", "notosanstc", "notoserif", "msung", "stsong" }
                .Any(lower.Contains))
                family = null;
            else if (new[] { "cmsy", "cmmi", "cmex", "msam", "msbm", "symbol", "math", "dingbat", "wingding", "stix" }.Any(lower.Contains))
                family = null;
            else if (new[] { "times", "nimbusrom", "termes", "cmr", "cmbx", "cmti", "cmsl", "lmroman", "nimbus roman", "dejavuserif", "liberationserif", "tinos" }.Any(lower.Contains))
                family = "Times New Roman";
            else if (new[] { "helvetica", "arial", "nimbussan", "heros", "liberationsans", "arimo", "cmss" }.Any(lower.Contains))
                family = "Arial";
            else if (new[] { "courier", "cmtt", "nimbusmon", "cousine", "liberationmono", "mono" }.Any(lower.Contains))
                family = "Courier New";
            else
            {
                // "Calibri-Bold", "Georgia,Italic", "SegoeUI-Semibold", "ArialMT" → the family part.
                string family0 = name.Split('-', ',')[0];
                foreach (var suffix in new[] { "PSMT", "MT", "PS", "Std", "Pro" })
                    if (family0.EndsWith(suffix) && family0.Length > suffix.Length + 2) family0 = family0.Substring(0, family0.Length - suffix.Length);
                family = family0.Length >= 3 && family0.All(c => char.IsLetter(c) || c == ' ') ? family0 : null;
            }
            FamilyCache[pdfName] = family;
            return family;
        }
    }

    private static void Append(List<DocRun> runs, string text, DocRun style)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0 && (runs[^1].SameStyle(style) || text.Trim().Length == 0))
        {
            runs[^1].Text += text;
            return;
        }
        runs.Add(style.Copy(text));
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
            result.Add(r.Copy(r.Text.Substring(count)));
            count = 0;
        }
        if (result.Count > 0) result[0].Text = result[0].Text.TrimStart();
        return result;
    }

    private static PdfRectangle Union(PdfRectangle a, PdfRectangle b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));

    private static bool Intersects(PdfRectangle a, PdfRectangle b, double pad = 0) =>
        a.Left - pad < b.Right && b.Left - pad < a.Right && a.Bottom - pad < b.Top && b.Bottom - pad < a.Top;

    private static bool Contains(PdfRectangle r, double x, double y, double pad = 0) =>
        x >= r.Left - pad && x <= r.Right + pad && y >= r.Bottom - pad && y <= r.Top + pad;

    // MARK: - Lines

    /// <summary>
    /// Words sharing a baseline form a row; a row is cut wherever a gap is much wider than a space, so the two
    /// columns of a page, or the cells of a table, become separate pieces.
    /// </summary>
    private static List<Seg> Segments(List<Pig.Content.Word> words, (double left, double right)? gutter)
    {
        var infos = new List<(Pig.Content.Word word, List<Pig.Content.Letter> letters, double baseline, double size)>();
        foreach (var w in words)
        {
            var letters = w.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (letters.Count == 0) continue;
            double size = letters.Select(l => l.PointSize).OrderBy(v => v).ElementAt(letters.Count / 2);
            double baseline = letters.Select(l => l.StartBaseLine.Y).OrderBy(v => v).ElementAt(letters.Count / 2);
            infos.Add((w, letters, baseline, size));
        }

        var rows = new List<(double baseline, double size, List<int> members)>();
        foreach (int i in Enumerable.Range(0, infos.Count).OrderByDescending(i => infos[i].baseline))
        {
            var info = infos[i];
            int found = -1;
            for (int r = rows.Count - 1; r >= 0 && r >= rows.Count - 4; r--)
            {
                if (Math.Abs(rows[r].baseline - info.baseline) <= Math.Max(rows[r].size, info.size) * 0.4) found = r;
            }
            if (found < 0) rows.Add((info.baseline, info.size, new List<int> { i }));
            else rows[found].members.Add(i);
        }

        var result = new List<Seg>();
        foreach (var row in rows)
        {
            var members = row.members.OrderBy(i => infos[i].word.BoundingBox.Left).ToList();
            Seg? seg = null;
            double lastRight = 0;
            foreach (int i in members)
            {
                var (word, letters, baseline, size) = infos[i];
                double em = Math.Max(4, size);
                double gap = word.BoundingBox.Left - lastRight;
                // A gap over the column gutter always separates, even when the gutter is narrow.
                bool acrossGutter = gutter is (double gl, double gr) && lastRight <= (gl + gr) / 2 + 1 && word.BoundingBox.Left >= (gl + gr) / 2 - 1
                                    && gap > Math.Max(4, (gr - gl) * 0.6);
                if (seg != null && (gap > Math.Max(7, em * 1.15) || acrossGutter))
                {
                    result.Add(seg);
                    seg = null;
                }
                if (seg == null)
                {
                    seg = new Seg { Box = word.BoundingBox, Baseline = row.baseline };
                }
                else
                {
                    if (gap > em * 0.15)
                    {
                        Append(seg.Runs, " ", StyleOf(letters[0]));
                        seg.Text += " ";
                    }
                    seg.Box = Union(seg.Box, word.BoundingBox);
                }
                foreach (var letter in letters)
                {
                    Append(seg.Runs, letter.Value, StyleOf(letter));
                    seg.Text += letter.Value;
                    seg.Letters++;
                    if (IsMathFont(letter.FontName)) seg.MathLetters++;
                }
                lastRight = word.BoundingBox.Right;
            }
            if (seg != null) result.Add(seg);
        }
        foreach (var seg in result)
        {
            var sizes = seg.Runs.Where(r => r.Text.Trim().Length > 0 && r.Size > 0).SelectMany(r => Enumerable.Repeat(r.Size, r.Text.Length)).OrderBy(v => v).ToList();
            seg.Size = sizes.Count > 0 ? sizes[sizes.Count / 2] : Math.Max(6, seg.Box.Height);
        }

        // Bullets: strip the mark; a mark standing on its own belongs to the piece just right of it.
        var marks = new List<Seg>();
        foreach (var seg in result)
        {
            string rest = StripBullet(seg.Text, out bool bullet);
            if (!bullet) continue;
            if (rest.Trim().Length == 0)
            {
                marks.Add(seg);
                continue;
            }
            seg.Runs = RemovePrefix(seg.Runs, seg.Text.Length - rest.Length);
            seg.Text = rest;
            seg.Bullet = true;
        }
        foreach (var mark in marks)
        {
            result.Remove(mark);
            double mid = (mark.Box.Top + mark.Box.Bottom) / 2;
            var owner = result.Where(s => s.Box.Left >= mark.Box.Left && s.Box.Left - mark.Box.Right < 40 && mid <= s.Box.Top + 2 && mid >= s.Box.Bottom - 2)
                .OrderBy(s => s.Box.Left - mark.Box.Right).FirstOrDefault();
            if (owner != null) owner.Bullet = true;
        }
        return result;
    }

    // MARK: - Figures

    /// <summary>
    /// Figures: clusters of drawn shapes and pictures that touch each other. Text inside a cluster is the
    /// figure's labels and goes with it. A lone shape, a thin rule, a page background or a box full of text
    /// is not a figure.
    /// </summary>
    private static List<(PdfRectangle rect, bool hasImage, Pig.Content.IPdfImage? onlyImage)> Figures(Pig.Content.Page page, List<Seg> segs, List<PdfRectangle> tables, (double left, double right)? gutter)
    {
        // Two figures side by side in different columns stay two figures.
        bool SameSide(PdfRectangle a, PdfRectangle b)
        {
            if (gutter is not (double gl, double gr)) return true;
            double mid = (gl + gr) / 2;
            bool aLeft = a.Right <= mid + 2, aRight = a.Left >= mid - 2, bLeft = b.Right <= mid + 2, bRight = b.Left >= mid - 2;
            return !((aLeft && bRight) || (aRight && bLeft));
        }
        double pageArea = page.Width * page.Height;
        var boxes = new List<(PdfRectangle r, Pig.Content.IPdfImage? image)>();
        try
        {
            foreach (var path in page.ExperimentalAccess.Paths)
            {
                if (path.IsClipping || !(path.IsFilled || path.IsStroked)) continue;
                var b = path.GetBoundingRectangle();
                if (b == null) continue;
                var r = b.Value;
                if (r.Width * r.Height > pageArea * 0.8) continue;
                // White fills are usually just the paper colour behind something else.
                if (path.IsFilled && !path.IsStroked && path.FillColor != null)
                {
                    var (cr, cg, cb) = path.FillColor.ToRGBValues();
                    if (cr > 0.97 && cg > 0.97 && cb > 0.97) continue;
                }
                boxes.Add((r, null));
            }
        }
        catch
        {
            // drawings PdfPig can't read: pictures only
        }
        foreach (var image in page.GetImages())
        {
            var r = image.Bounds;
            if (r.Width < 6 || r.Height < 6) continue;
            if (segs.Count > 0 && r.Width * r.Height > pageArea * 0.8) continue;
            boxes.Add((r, image));
        }
        if (boxes.Count == 0) return new();

        // Union-find over boxes that touch (within 4 pt).
        var parent = Enumerable.Range(0, boxes.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var order = Enumerable.Range(0, boxes.Count).OrderBy(i => boxes[i].r.Left).ToArray();
        for (int a = 0; a < order.Length; a++)
        {
            var ra = boxes[order[a]].r;
            for (int b = a + 1; b < order.Length; b++)
            {
                var rb = boxes[order[b]].r;
                if (rb.Left - 4 > ra.Right) break;
                if (Intersects(ra, rb, 4) && SameSide(ra, rb)) parent[Find(order[a])] = Find(order[b]);
            }
        }

        var result = new List<(PdfRectangle, bool, Pig.Content.IPdfImage?)>();
        foreach (var group in Enumerable.Range(0, boxes.Count).GroupBy(Find))
        {
            var members = group.Select(i => boxes[i]).ToList();
            var rect = members.Select(m => m.r).Aggregate(Union);
            bool hasImage = members.Any(m => m.image != null);
            if (rect.Width < 24 || rect.Height < 18) continue;
            if (rect.Width * rect.Height > pageArea * 0.85) continue;
            if (!hasImage && members.Count < 3) continue;
            // Labels: short text inside or hugging the drawing. Captions and body lines never count.
            var inside = segs.Where(s => !s.Used && IsLabel(s) && Contains(rect, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 2)).ToList();
            if (!hasImage)
            {
                // A frame or table grid full of text stays text.
                double textArea = inside.Sum(s => s.Box.Width * s.Box.Height);
                int chars = inside.Sum(s => s.Text.Length);
                if (chars > 220 || textArea > rect.Width * rect.Height * 0.3) continue;
                // So does a ruled table — unless the drawing has real shapes (bars, curves, boxes), as a chart does.
                int shapes = members.Count(m => m.r.Width > 2.5 && m.r.Height > 2.5);
                if (shapes < 3 && tables.Any(t => Intersects(t, rect) && Overlap(t, rect) > Area(t) * 0.4)) continue;
            }
            result.Add((rect, hasImage, members.Count == 1 ? members[0].image : null));
        }

        // Grow each figure by the labels around it, and join figures that end up next to each other,
        // until nothing changes (an icon, its caption word and the diagram beside it are one figure).
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < result.Count; i++)
            {
                var rect = result[i].Item1;
                foreach (var s in segs.Where(s => !s.Used && IsLabel(s) && Intersects(rect, s.Box, 8)))
                {
                    s.Used = true;
                    rect = Union(rect, s.Box);
                    changed = true;
                }
                result[i] = (rect, result[i].Item2, result[i].Item3);
            }
            for (int i = 0; i < result.Count && !changed; i++)
                for (int j = i + 1; j < result.Count && !changed; j++)
                    if ((Intersects(result[i].Item1, result[j].Item1, 12) || SideBySide(result[i].Item1, result[j].Item1)) && SameSide(result[i].Item1, result[j].Item1))
                    {
                        result[i] = (Union(result[i].Item1, result[j].Item1), result[i].Item2 || result[j].Item2, null);
                        result.RemoveAt(j);
                        changed = true;
                    }
        }
        foreach (var f in result)
            Trace?.Invoke($"figure p{page.Number}: {f.Item1.Left:0},{f.Item1.Bottom:0}-{f.Item1.Right:0},{f.Item1.Top:0} image={f.Item2}");
        return result;
    }

    /// <summary>Panels of one figure: next to each other, mostly at the same height, a small gap apart.</summary>
    private static bool SideBySide(PdfRectangle a, PdfRectangle b)
    {
        double overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        double gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        return overlap > Math.Min(a.Height, b.Height) * 0.5 && gap < 40;
    }

    private static double Area(PdfRectangle r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);

    private static double Overlap(PdfRectangle a, PdfRectangle b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));

    private static readonly string[] CaptionStarts = { "Fig", "FIG", "Figure", "FIGURE", "Table", "TABLE", "圖", "表", "Algorithm" };

    /// <summary>A short piece of text that can be part of a figure: not a caption, not a sentence of the body.</summary>
    private static bool IsLabel(Seg s) => s.Text.Length <= 40 && !CaptionStarts.Any(c => s.Text.StartsWith(c));

    /// <summary>The picture's own bytes, for when nothing can render the page.</summary>
    private static DocImage? Decode(Pig.Content.IPdfImage image)
    {
        var b = image.Bounds;
        try
        {
            if (image.TryGetPng(out var png)) return new DocImage(png, "png", b.Width, b.Height);
            var raw = image.RawBytes;
            if (raw.Length > 4 && raw[0] == 0xFF && raw[1] == 0xD8 && image.ColorSpaceDetails?.NumberOfColorComponents != 4)
                return new DocImage(raw.ToArray(), "jpeg", b.Width, b.Height);
        }
        catch
        {
            // undecodable
        }
        return null;
    }

    // MARK: - Equations

    private static bool IsMathFont(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        return new[] { "cmmi", "cmsy", "cmex", "msam", "msbm", "math", "symbol", "stix", "cmbsy", "cmmib", "eufm", "rsfs", "dsrom", "bbold" }.Any(lower.Contains);
    }

    private static readonly System.Text.RegularExpressions.Regex EquationNumber = new(@"^\(\d{1,3}[a-z]?\)$");

    /// <summary>
    /// Display equations: lines set mostly in maths fonts, with the bits around them (sub- and superscripts,
    /// fraction parts, the equation number). Word can't rebuild them from a PDF, so they become pictures.
    /// </summary>
    private static List<PdfRectangle> Equations(List<Seg> segs, (double left, double right)? gutter)
    {
        var mathy = segs.Where(s => !s.Used && s.Letters > 0 && s.MathLetters * 2 >= s.Letters && !EquationNumber.IsMatch(s.Text.Trim())).ToList();
        var groups = new List<(PdfRectangle rect, int column)>();
        foreach (var seg in mathy.OrderByDescending(s => s.Box.Top))
        {
            int column = ColumnOf(seg.Box, gutter);
            double em = Math.Max(6, seg.Size);
            int at = groups.FindIndex(g => g.column == column && seg.Box.Top >= g.rect.Bottom - em * 1.6 && seg.Box.Bottom <= g.rect.Top + em);
            if (at >= 0) groups[at] = (Union(groups[at].rect, seg.Box), column);
            else groups.Add((seg.Box, column));
        }
        var result = new List<PdfRectangle>();
        foreach (var (start, column) in groups)
        {
            var rect = start;
            // Everything small inside or beside the formula on its rows belongs to it: indices, fraction parts, "(3)".
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var s in segs.Where(s => !s.Used && ColumnOf(s.Box, gutter) == column && s.Box.Top > rect.Bottom - 1 && s.Box.Bottom < rect.Top + 1))
                {
                    double mid = (s.Box.Top + s.Box.Bottom) / 2;
                    bool small = s.Text.Length <= 12 || s.MathLetters * 2 >= s.Letters || EquationNumber.IsMatch(s.Text.Trim());
                    if (!small || mid < rect.Bottom - 1 || mid > rect.Top + 1) continue;
                    s.Used = true;
                    rect = Union(rect, s.Box);
                    grew = true;
                }
            }
            // A lone symbol inside running text is not a display equation.
            if (rect.Width < 30 && rect.Height < 14) continue;
            foreach (var s in segs.Where(s => !s.Used && s.MathLetters * 2 >= s.Letters && Contains(rect, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1))) s.Used = true;
            foreach (var s in mathy) if (Contains(rect, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1)) s.Used = true;
            Trace?.Invoke($"equation: {rect.Left:0},{rect.Bottom:0}-{rect.Right:0},{rect.Top:0}");
            result.Add(new PdfRectangle(rect.Left - 1, rect.Bottom - 2, rect.Right + 1, rect.Top + 2));
        }
        return result;
    }

    // MARK: - Tables

    /// <summary>
    /// Tables: two or more consecutive rows that each hold the same number (2+) of separate, short pieces of text
    /// lined up under one another.
    /// </summary>
    private static List<Item> Tables(List<Seg> segs)
    {
        var result = new List<Item>();
        var rows = new List<List<Seg>>();
        foreach (var seg in segs.Where(s => !s.Used).OrderByDescending(s => s.Baseline))
        {
            var row = rows.LastOrDefault();
            if (row != null && Math.Abs(row[0].Baseline - seg.Baseline) < Math.Max(row[0].Size, seg.Size) * 0.4)
            {
                row.Add(seg);
                continue;
            }
            rows.Add(new List<Seg> { seg });
        }
        foreach (var row in rows) row.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));

        bool Cells(List<Seg> row) => row.Count >= 2;

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
                double pitch = prev[0].Baseline - rows[end][0].Baseline;
                if (pitch > Math.Max(prev[0].Size, 6) * 4) break;
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
            bool shortCells = cells.Average(c => c.Text.Length) < 40 && cells.Max(c => c.Box.Width) < Math.Max(span * 0.6, 120)
                              // A line of running text from the other column is never a cell.
                              && cells.All(c => c.Text.Length <= 60 && c.Box.Width < 190);
            if (group.Count >= 2 && shortCells)
            {
                var table = new DocTable();
                double em = Math.Max(8, cells.Average(c => c.Size));
                for (int k = 0; k < n; k++)
                    table.Widths.Add(Math.Round(Math.Max(em * 3, k + 1 < n ? columns[k + 1].left - columns[k].left : columns[k].right - columns[k].left + em * 1.5) + 8));
                foreach (var row in group)
                {
                    table.Rows.Add(row.Select(l => l.Runs.Select(x => x.Copy(TextFix.Radicals(x.Text))).ToList()).ToList());
                    foreach (var l in row) l.Used = true;
                }
                var item = new Item { Top = group[0].Max(l => l.Box.Top), Size = group[0][0].Size, Box = cells.Select(c => c.Box).Aggregate(Union) };
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

    // MARK: - Columns and reading order

    /// <summary>
    /// The gutter between two columns: a vertical strip near the middle of the page that (almost) no text row
    /// crosses, with plenty of text on both sides. Titles and full-width captions may cross it.
    /// </summary>
    private static (double left, double right)? Gutter(List<Pig.Content.Word> words)
    {
        if (words.Count < 40) return null;
        double left = words.Min(w => w.BoundingBox.Left), right = words.Max(w => w.BoundingBox.Right), width = right - left;
        if (width < 200) return null;
        // Rows of words (by rounded baseline), so a long title counts once, not once per word.
        var rows = words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3)).Select(g => g.Select(w => w.BoundingBox).ToList()).ToList();
        int from = (int)(left + width * 0.3), to = (int)(left + width * 0.7);
        // Titles, author lines and wide captions cross the gutter; a few such rows are fine.
        int allowed = Math.Max(2, (int)(rows.Count * 0.15));
        int bestStart = -1, bestLength = 0, runStart = -1;
        for (int x = from; x <= to; x++)
        {
            int crossing = rows.Count(r => r.Any(b => b.Left < x && b.Right > x));
            if (crossing <= allowed)
            {
                if (runStart < 0) runStart = x;
                if (x - runStart + 1 > bestLength)
                {
                    bestLength = x - runStart + 1;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }
        if (bestLength < 4) return null;
        double gl = bestStart, gr = bestStart + bestLength - 1;
        int leftRows = rows.Count(r => r.Any(b => b.Right <= gl + 1 && b.Left < gl - width * 0.2));
        int rightRows = rows.Count(r => r.Any(b => b.Left >= gr - 1 && b.Right > gr + width * 0.2));
        if (leftRows < 8 || rightRows < 8) return null;
        return (gl, gr);
    }

    private static int ColumnOf(PdfRectangle box, (double left, double right)? gutter)
    {
        if (gutter is not (double gl, double gr)) return -1;
        double mid = (gl + gr) / 2;
        if (box.Right <= mid + 2) return 0;
        if (box.Left >= mid - 2) return 1;
        return -1;
    }

    /// <summary>Top to bottom; between two full-width pieces, the whole left column before the right one.</summary>
    private static List<Seg> ReadingOrder(List<Seg> segs)
    {
        var sorted = segs.OrderByDescending(s => s.Box.Top).ThenBy(s => s.Box.Left).ToList();
        var result = new List<Seg>();
        var band = new List<Seg>();
        void Flush()
        {
            result.AddRange(band.Where(s => s.Column == 0).OrderByDescending(s => s.Box.Top).ThenBy(s => s.Box.Left));
            result.AddRange(band.Where(s => s.Column == 1).OrderByDescending(s => s.Box.Top).ThenBy(s => s.Box.Left));
            band.Clear();
        }
        foreach (var s in sorted)
        {
            if (s.Column < 0)
            {
                // A full-width piece only ends the band when it is below everything in it so far … or the band is empty.
                Flush();
                result.Add(s);
            }
            else
            {
                band.Add(s);
            }
        }
        Flush();
        return result;
    }

    // MARK: - Paragraphs

    /// <summary>First halves of words usually written with a hyphen, kept when a line break falls after them.</summary>
    private static readonly HashSet<string> CompoundHeads = new()
    {
        "self", "data", "fine", "coarse", "state", "real", "high", "low", "well", "non", "pre", "post", "co", "cross", "multi", "semi",
        "end", "open", "user", "time", "long", "short", "large", "small", "one", "two", "three", "deep", "top", "first", "second",
        "third", "full", "half", "rule", "learning", "zero", "few", "many", "anti", "inter", "intra", "out", "in", "on", "off",
        "e", "x", "t", "k", "n", "re", "de", "un", "over", "under", "mid", "peer", "ground", "black", "white", "light", "heavy",
    };

    private static bool JoinsTight(string before, string after) => TextFix.JoinsTight(before, after);

    /// <summary>
    /// Pieces in reading order → paragraphs. A new paragraph starts at a bullet, a column change, a size change,
    /// a clearly bigger gap, an indent change, a short line before, or a switch between bold and regular.
    /// A word broken with a hyphen at the end of a line is joined again.
    /// </summary>
    private static List<Item> Paragraphs(List<Seg> ordered, Func<int, (double left, double right)> columnExtent)
    {
        var gapsByColumn = new Dictionary<int, double>();
        foreach (var group in ordered.Where(s => s.Block == null).GroupBy(s => s.Column))
        {
            var list = group.ToList();
            var gaps = list.Zip(list.Skip(1), (a, b) => (a, b))
                .Where(p => Math.Abs(p.a.Size - p.b.Size) < 0.6)
                .Select(p => p.a.Box.Bottom - p.b.Box.Top).Where(g => g > -2).OrderBy(g => g).ToList();
            gapsByColumn[group.Key] = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
        }

        // The usual baseline-to-baseline distance in each column, for exact line spacing.
        var pitchByColumn = new Dictionary<int, double>();
        foreach (var group in ordered.Where(s => s.Block == null).GroupBy(s => s.Column))
        {
            var list = group.ToList();
            var pitches = list.Zip(list.Skip(1), (a, b) => (a, b))
                .Where(p => Math.Abs(p.a.Size - p.b.Size) < 0.6 && p.a.Baseline > p.b.Baseline)
                .Select(p => p.a.Baseline - p.b.Baseline).Where(d => d < p_max(list)).OrderBy(d => d).ToList();
            if (pitches.Count > 0) pitchByColumn[group.Key] = pitches[pitches.Count / 2];
        }
        static double p_max(List<Seg> list) => list.Count > 0 ? list.Average(s => s.Size) * 2.2 : 30;

        var result = new List<Item>();
        Item? current = null;
        Seg? previous = null;
        foreach (var seg in ordered)
        {
            if (seg.Block != null)
            {
                result.Add(seg.Block);
                current = null;
                previous = null;
                continue;
            }
            bool split = current == null || previous == null || seg.Bullet || seg.Column != previous.Column;
            if (!split)
            {
                var prev = previous!;
                double gap = prev.Box.Bottom - seg.Box.Top;
                double em = Math.Max(4, Math.Max(seg.Size, prev.Size));
                double typical = gapsByColumn.TryGetValue(seg.Column, out var t) ? t : 0;
                double limit = Math.Max(em * 0.85, typical * 1.6 + 1);
                var (colLeft, colRight) = columnExtent(seg.Column);
                bool previousShort = prev.Box.Right < colRight - Math.Max((colRight - colLeft) * 0.15, em * 3);
                bool boldNow = seg.Runs.All(r => r.Bold || r.Text.Trim().Length == 0), boldBefore = prev.Runs.All(r => r.Bold || r.Text.Trim().Length == 0);
                if (Math.Abs(seg.Size - prev.Size) > Math.Max(seg.Size, prev.Size) * 0.12) split = true;
                else if (gap > limit || gap < -em * 1.5) split = true;
                else if (seg.Box.Left - prev.Box.Left > em) split = true;
                else if (prev.Box.Left - seg.Box.Left > em && current!.Lines.Count > 1) split = true;
                else if (previousShort) split = true;
                else if (boldNow != boldBefore && (boldNow || current!.Para.Text.Length < 60)) split = true;
            }
            if (split)
            {
                // The space left above this paragraph, beyond ordinary line spacing, is the previous one's spacing after.
                if (current != null && previous != null && previous.Column == seg.Column)
                {
                    double gapAbove = previous.Box.Bottom - seg.Box.Top;
                    double typicalGap = gapsByColumn.TryGetValue(seg.Column, out var tg) ? tg : 0;
                    if (gapAbove >= -1) current.Para.SpaceAfter = Math.Round(Math.Clamp(gapAbove - typicalGap, 0, 30));
                }
                current = new Item { Top = seg.Box.Top, Size = seg.Size, Column = seg.Column };
                if (pitchByColumn.TryGetValue(seg.Column, out var pitch) && pitch > seg.Size * 0.9 && pitch < seg.Size * 1.8) current.Pitch = pitch;
                current.Baselines.Add(seg.Baseline);
                current.Para.Bullet = seg.Bullet;
                foreach (var r in seg.Runs) Append(current.Para.Runs, r.Text, r);
                current.Lines.Add(seg.Box);
                result.Add(current);
            }
            else
            {
                var runs = current!.Para.Runs;
                string text = current.Para.Text;
                var last = runs[^1];
                // "net-" + "work" → "network", but "data-" + "driven" stays "data-driven".
                if (text.Length >= 2 && text[^1] == '-' && char.IsLetter(text[^2]) && seg.Text.Length > 0 && char.IsLower(seg.Text[0]))
                {
                    int start = text.LastIndexOfAny(new[] { ' ', '(', '[', '/', '—' }, text.Length - 2) + 1;
                    string fragment = text.Substring(start, text.Length - 1 - start).ToLowerInvariant();
                    if (!CompoundHeads.Contains(fragment)) last.Text = last.Text.Substring(0, last.Text.Length - 1);
                }
                else if (!JoinsTight(text, seg.Text))
                    Append(runs, " ", last);
                foreach (var r in seg.Runs) Append(runs, r.Text, r);
                current.Lines.Add(seg.Box);
                current.Baselines.Add(seg.Baseline);
            }
            previous = seg;
        }
        return result;
    }

    // MARK: - Whole document

    public static async Task<DocModel> Extract(string path, RegionRenderer? render)
    {
        var items = new List<Item>();
        var pageSizes = new List<(double w, double h)>();
        var gutters = new List<double>();
        try
        {
            // Clipping applied, so a chart's curves end at its axes instead of stretching across the page.
            using var doc = Pig.PdfDocument.Open(path, new Pig.ParsingOptions { ClipPaths = true });
            int pageIndex = 0;
            foreach (var page in doc.GetPages())
            {
                pageSizes.Add((page.Width, page.Height));
                try
                {
                    var pageItems = await PageItems(page, pageIndex, render);
                    if (pageItems.gutter is double g) gutters.Add(g);
                    items.AddRange(pageItems.items);
                }
                catch (Exception e) when (e is not FWError)
                {
                    Log.Write("pdf: page " + (pageIndex + 1) + " layout failed: " + e.Message);
                    string text = page.Text;
                    if (text.Trim().Length > 0)
                    {
                        var item = new Item { Top = page.Height, Page = pageIndex };
                        item.Para.Runs.Add(new DocRun(text));
                        items.Add(item);
                    }
                }
                pageIndex++;
            }
        }
        catch (Exception e) when (e is not FWError)
        {
            throw e.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || e.Message.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0
                ? new FWError("這個 PDF 有密碼保護")
                : new FWError("打不開這個 PDF：" + Fmt.Trunc(e.Message, 80));
        }
        return Layout(items, pageSizes, gutters);
    }

    private static async Task<(List<Item> items, double? gutter)> PageItems(Pig.Content.Page page, int pageIndex, RegionRenderer? render)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var gutter = Gutter(words);
        Trace?.Invoke($"page {pageIndex + 1}: gutter {(gutter is (double ga, double gb) ? $"{ga:0}-{gb:0}" : "none")}");
        var segs = Segments(words, gutter);

        // Tables first (text grids stay editable), then figures with their labels.
        var blocks = new List<Item>();
        var tables = Tables(segs);
        foreach (var (rect, hasImage, onlyImage) in Figures(page, segs, tables.Select(t => t.Box!.Value).ToList(), gutter))
        {
            DocImage? image = null;
            if (render != null) image = await render(pageIndex, rect, page.Width, page.Height);
            if (image == null && onlyImage != null) image = Decode(onlyImage);
            if (image == null)
            {
                // Can't draw it: give its labels back to the text.
                foreach (var s in segs.Where(s => s.Used && s.Block == null && Contains(rect, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1)
                                                  && !tables.Any(t => t.Box is PdfRectangle tb && Contains(tb, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1))))
                    s.Used = false;
                continue;
            }
            image.Width = rect.Width;
            image.Height = rect.Height;
            var item = new Item { Top = rect.Top, Box = rect };
            item.Para.Image = image;
            blocks.Add(item);
        }
        foreach (var rect in Equations(segs, gutter))
        {
            DocImage? image = render != null ? await render(pageIndex, rect, page.Width, page.Height) : null;
            if (image == null)
            {
                foreach (var s in segs.Where(s => s.Used && Contains(rect, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1)
                                                  && !tables.Any(t => t.Box is PdfRectangle tb && Contains(tb, (s.Box.Left + s.Box.Right) / 2, (s.Box.Top + s.Box.Bottom) / 2, 1))))
                    s.Used = false;
                continue;
            }
            image.Width = rect.Width;
            image.Height = rect.Height;
            var item = new Item { Top = rect.Top, Box = rect };
            item.Para.Image = image;
            blocks.Add(item);
        }
        // A table that turned out to sit inside a figure is part of the picture.
        tables.RemoveAll(t => blocks.Any(b => Overlap(b.Box!.Value, t.Box!.Value) > Area(t.Box!.Value) * 0.6));
        blocks.AddRange(tables);
        var text = segs.Where(s => !s.Used).ToList();
        foreach (var s in text) s.Column = ColumnOf(s.Box, gutter);
        foreach (var block in blocks)
        {
            block.Column = ColumnOf(block.Box!.Value, gutter);
            text.Add(new Seg { Box = block.Box.Value, Block = block, Column = block.Column, Text = "" });
        }
        var ordered = ReadingOrder(text);

        // Column extents, for alignment and short-line checks.
        var all = text.Where(s => s.Block == null).ToList();
        double contentLeft = all.Count > 0 ? all.Min(s => s.Box.Left) : 0, contentRight = all.Count > 0 ? all.Max(s => s.Box.Right) : page.Width;
        (double, double) Extent(int column)
        {
            if (column < 0 || gutter is not (double gl, double gr)) return (contentLeft, contentRight);
            var inColumn = all.Where(s => s.Column == column).ToList();
            if (inColumn.Count == 0) return column == 0 ? (contentLeft, gl) : (gr, contentRight);
            return (inColumn.Min(s => s.Box.Left), inColumn.Max(s => s.Box.Right));
        }

        var items = Paragraphs(ordered, Extent);
        foreach (var item in items)
        {
            item.Page = pageIndex;
            item.TwoColumns = gutter != null && item.Column >= 0;
            var (left, right) = Extent(item.Column);
            Align(item, left, right);
        }
        return (items, gutter is (double a, double b) ? b - a : null);
    }

    /// <summary>Alignment and indent of one item, measured against its column.</summary>
    private static void Align(Item item, double left, double right)
    {
        double width = Math.Max(50, right - left);
        var p = item.Para;
        if (p.Table is DocTable table)
        {
            double l = item.Lines.Min(x => x.Left), r = item.Lines.Max(x => x.Right);
            double tolerance = Math.Max(6, width * 0.04);
            if (Math.Abs((l - left) - (right - r)) < tolerance * 2 && l - left > tolerance * 2) table.Align = DocAlign.Center;
            else if (l - left > 8) table.Indent = Math.Round(l - left);
            return;
        }
        if (item.Box is PdfRectangle b && p.Image != null)
        {
            double mid = (b.Left + b.Right) / 2;
            if (Math.Abs(mid - (left + right) / 2) < width * 0.1 && b.Width < width * 0.92) p.Align = DocAlign.Center;
            else if (b.Left - left > 8) p.Indent = Math.Min(b.Left - left, width * 0.6);
            return;
        }
        if (item.Lines.Count == 0) return;
        // First-line indent: the opening line starts further right than the rest.
        if (item.Lines.Count >= 2 && item.Lines[0].Left - item.Lines[1].Left > Math.Max(3, item.Size * 0.5))
            p.FirstIndent = Math.Round(item.Lines[0].Left - item.Lines[1].Left);
        double tol = Math.Max(4, width * 0.03);
        double minLeft = (item.Lines.Count >= 2 ? item.Lines.Skip(1) : item.Lines).Min(l => l.Left), maxRight = item.Lines.Max(l => l.Right);
        bool centered = item.Lines.All(l => Math.Abs((l.Left - left) - (right - l.Right)) < tol * 2) && minLeft - left > tol * 2;
        if (centered) p.Align = DocAlign.Center;
        else if (right - maxRight < tol && minLeft - left > width * 0.35) p.Align = DocAlign.Right;
        // Justified: every line but the last runs to the right edge of the column.
        else if (item.Lines.Count >= 3 && item.Lines.Take(item.Lines.Count - 1).All(l => right - l.Right < Math.Max(2.5, item.Size * 0.3))) p.Align = DocAlign.Justify;
        else if (minLeft - left > 8) p.Indent = Math.Round(Math.Min(minLeft - left, width * 0.6));
    }

    /// <summary>Headings, page breaks and page geometry for the collected items.</summary>
    private static DocModel Layout(List<Item> items, List<(double w, double h)> pageSizes, List<double> gutters)
    {
        var model = new DocModel();
        double bodySize = items.Where(i => i.Size > 0 && i.Para.Image == null && i.Para.Table == null)
            .GroupBy(i => Math.Round(i.Size))
            .OrderByDescending(g => g.Sum(i => i.Para.Text.Length))
            .Select(g => g.Key)
            .FirstOrDefault();
        if (bodySize <= 0) bodySize = 12;

        // Running headers and footers (page numbers, a repeated title) are page furniture, not content.
        double Bottom(Item i) => i.Box?.Bottom ?? (i.Lines.Count > 0 ? i.Lines.Min(l => l.Bottom) : 0);
        double TopOf(Item i) => i.Box?.Top ?? (i.Lines.Count > 0 ? i.Lines.Max(l => l.Top) : 0);
        string Key(Item i) => System.Text.RegularExpressions.Regex.Replace(i.Para.Text.Trim(), @"\d+", "#");
        bool InMargin(Item i)
        {
            if (i.Para.Image != null || i.Para.Table != null || i.Page >= pageSizes.Count || i.Para.Text.Trim().Length > 80) return false;
            double h = pageSizes[i.Page].h;
            return Bottom(i) > h * 0.93 || TopOf(i) < h * 0.07;
        }
        var marginItems = items.Where(InMargin).ToList();
        var repeated = marginItems.GroupBy(Key).Where(g => g.Select(i => i.Page).Distinct().Count() >= Math.Max(3, pageSizes.Count * 0.4)).Select(g => g.Key).ToHashSet();
        items = items.Where(i => !(InMargin(i) && (repeated.Contains(Key(i)) || System.Text.RegularExpressions.Regex.IsMatch(i.Para.Text.Trim(), @"^(\d{1,4}|[ivxlcIVXLC]{1,6}|- ?\d{1,4} ?-|\d{1,4} ?/ ?\d{1,4})$")))).ToList();

        // A new page only where the PDF page ended early (a title page, the end of a chapter); otherwise the
        // text flows on, so a few lines that wrap differently in Word never leave a half-empty page behind.
        var lowest = items.GroupBy(i => i.Page).ToDictionary(g => g.Key, g => g.Min(Bottom));
        int lastPage = -1;
        foreach (var item in items)
        {
            var p = item.Para;
            if (item.Page != lastPage)
            {
                p.PageBreak = lastPage >= 0 && lastPage < pageSizes.Count && lowest.TryGetValue(lastPage, out var low) && low > pageSizes[lastPage].h * 0.3;
                lastPage = item.Page;
            }
            p.Columns = item.TwoColumns ? 2 : 1;
            // Exact line spacing as in the PDF: the paragraph's own line pitch, or the column's for one-liners.
            if (item.Baselines.Count >= 2)
            {
                var pitches = item.Baselines.Zip(item.Baselines.Skip(1), (a, b) => a - b).Where(d => d > 0).OrderBy(d => d).ToList();
                if (pitches.Count > 0 && pitches[pitches.Count / 2] < item.Size * 1.8) p.LineSpacing = Math.Round(pitches[pitches.Count / 2] * 2) / 2;
            }
            else if (item.Pitch > 0)
            {
                p.LineSpacing = Math.Round(item.Pitch * 2) / 2;
            }
            if (p.Image != null || p.Table != null)
            {
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
            model.Paras.Add(p);
        }

        if (pageSizes.Count > 0)
        {
            var first = pageSizes[0];
            var lines = items.SelectMany(i => i.Lines).ToList();
            double contentLeft = lines.Count > 0 ? lines.Min(l => l.Left) : 56;
            double contentRight = lines.Count > 0 ? lines.Max(l => l.Right) : first.w - 56;
            var firstLines = items.Where(i => i.Page == (items.FirstOrDefault()?.Page ?? 0)).SelectMany(i => i.Lines).ToList();
            double top = firstLines.Count > 0 ? first.h - firstLines.Max(l => l.Top) : 56;
            model.PageWidth = Math.Round(first.w, 1);
            model.PageHeight = Math.Round(first.h, 1);
            model.MarginLeft = Math.Clamp(Math.Round(contentLeft), 18, 108);
            model.MarginRight = Math.Clamp(Math.Round(first.w - contentRight), 18, 108);
            model.MarginTop = Math.Clamp(Math.Round(top), 18, 90);
            model.MarginBottom = Math.Clamp(Math.Round(model.MarginTop * 0.9), 18, 72);
            if (gutters.Count > 0) model.ColumnGap = Math.Round(gutters.OrderBy(g => g).ElementAt(gutters.Count / 2));
        }
        if (model.Paras.Count == 0) throw new FWError("這個 PDF 沒有文字層，試試「技能 → 文字辨識」");
        return model;
    }
}
