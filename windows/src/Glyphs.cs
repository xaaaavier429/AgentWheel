using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AgentWheel;

public enum GlyphKind
{
    Png, Jpg, Heic, Webp, Avif, Gif, Tiff, Pdf,
    LongImage, Word, Text, Markdown, Rtf, Html, Odt,
    Clapper, Film, FilmBadge, WebVideo, Loop, Note, Waves, WavesBadge, Voice, Vinyl, Bell,
    Compress, Shrink, Cutout, Scan, Mono, Rotate, Unpin, Backdrop,
    Split, Merge, Mute, Screen720, Cover, Fast, Slow,
    Speaker, Meter, Counter, Clipboard, Zip, Link, Tag, Fingerprint,
}

/// <summary>
/// Slot icons that say what the format *is*: a page with a transparency strip for PNG, a framed photo
/// for JPG, a browser for WebP… Light bodies, dark rims, a few colour accents, in a 100×100 box with y up.
/// </summary>
public sealed record Glyph(GlyphKind Kind, string Text = "", Color Tint = default)
{
    public static Glyph Of(string id) => id switch
    {
        "img.to.png" or "pdf.to.png" => new(GlyphKind.Png),
        "img.to.jpg" or "pdf.to.jpg" => new(GlyphKind.Jpg),
        "img.to.heic" => new(GlyphKind.Heic),
        "img.to.webp" => new(GlyphKind.Webp),
        "img.to.avif" => new(GlyphKind.Avif),
        "img.to.gif" => new(GlyphKind.Gif),
        "img.to.tiff" => new(GlyphKind.Tiff),
        "img.to.pdf" or "doc.to.pdf" => new(GlyphKind.Pdf),
        "pdf.to.long" => new(GlyphKind.LongImage),
        "pdf.to.docx" or "doc.to.docx" => new(GlyphKind.Word, "DOCX"),
        "doc.to.doc" => new(GlyphKind.Word, "DOC"),
        "pdf.to.txt" or "doc.to.txt" => new(GlyphKind.Text),
        "pdf.to.md" or "doc.to.md" => new(GlyphKind.Markdown),
        "pdf.to.rtf" or "doc.to.rtf" => new(GlyphKind.Rtf),
        "pdf.to.html" or "doc.to.html" => new(GlyphKind.Html),
        "doc.to.odt" => new(GlyphKind.Odt),
        "vid.to.mp4" => new(GlyphKind.Clapper),
        "vid.to.mov" => new(GlyphKind.Film),
        "vid.to.hevc" => new(GlyphKind.FilmBadge, "H.265"),
        "vid.to.webm" => new(GlyphKind.WebVideo),
        "vid.to.gif" => new(GlyphKind.Loop),
        "vid.to.mp3" or "aud.to.mp3" => new(GlyphKind.Note, "MP3", Ink.Orange),
        "vid.to.m4a" or "aud.to.m4a" => new(GlyphKind.Note, "M4A", Ink.Blue),
        "vid.to.wav" or "aud.to.wav" => new(GlyphKind.Waves),
        "aud.to.flac" => new(GlyphKind.WavesBadge, "FLAC", Ink.Gold),
        "aud.to.aiff" => new(GlyphKind.WavesBadge, "AIFF", Ink.Slate),
        "aud.to.opus" => new(GlyphKind.Voice),
        "aud.to.alac" => new(GlyphKind.Vinyl),
        "aud.to.m4r" => new(GlyphKind.Bell),
        "img.half" => new(GlyphKind.Shrink),
        "img.cutout" => new(GlyphKind.Cutout),
        "img.ocr" or "pdf.ocr" => new(GlyphKind.Scan),
        "img.mono" or "pdf.mono" => new(GlyphKind.Mono),
        "img.rotate" or "pdf.rotate" => new(GlyphKind.Rotate),
        "img.backdrop" => new(GlyphKind.Backdrop),
        "pdf.split" => new(GlyphKind.Split),
        "pdf.merge" => new(GlyphKind.Merge),
        "vid.mute" => new(GlyphKind.Mute),
        "vid.720p" => new(GlyphKind.Screen720),
        "vid.cover" => new(GlyphKind.Cover),
        "vid.fast" or "aud.fast" => new(GlyphKind.Fast),
        "vid.slow" => new(GlyphKind.Slow),
        "aud.mono" => new(GlyphKind.Speaker),
        "aud.loud" => new(GlyphKind.Meter),
        "doc.count" => new(GlyphKind.Counter),
        "doc.copy" => new(GlyphKind.Clipboard),
        "gen.zip" => new(GlyphKind.Zip),
        "gen.path" => new(GlyphKind.Link),
        "gen.name" => new(GlyphKind.Tag),
        "gen.sha" => new(GlyphKind.Fingerprint),
        _ when id.EndsWith(".compress") => new(GlyphKind.Compress),
        _ when id.EndsWith(".strip") => new(GlyphKind.Unpin),
        _ => new(GlyphKind.Text),
    };

    /// <summary>Draws the icon fitted into <paramref name="rect"/>.</summary>
    public void Draw(DrawingContext dc, Rect rect)
    {
        double side = Math.Min(rect.Width, rect.Height);
        double k = side / 100;
        double left = rect.X + (rect.Width - side) / 2;
        double top = rect.Y + (rect.Height - side) / 2;
        // y-up 100×100 box: x' = left + x·k, y' = top + side − y·k
        dc.PushTransform(new MatrixTransform(k, 0, 0, -k, left, top + side));
        new Painter(dc).Paint(this);
        dc.Pop();
    }
}

public static class Ink
{
    public static readonly Color Rim = Theme.Rgb(0.10, 0.11, 0.13);
    public static readonly (Color, Color) Paper = (Colors.White, Theme.Rgb(0.85, 0.87, 0.90));
    public static readonly (Color, Color) Metal = (Theme.Rgb(0.94, 0.95, 0.96), Theme.Rgb(0.64, 0.67, 0.71));
    public static readonly (Color, Color) ShadowMetal = (Theme.Rgb(0.80, 0.82, 0.85), Theme.Rgb(0.55, 0.58, 0.62));
    public static readonly (Color, Color) Dark = (Theme.Rgb(0.33, 0.35, 0.38), Theme.Rgb(0.13, 0.14, 0.16));
    public static readonly (Color, Color) Sky = (Theme.Rgb(0.33, 0.63, 0.93), Theme.Rgb(0.78, 0.90, 0.99));
    public static readonly Color Sun = Theme.Rgb(1.0, 0.82, 0.24);
    public static readonly Color HillBack = Theme.Rgb(0.55, 0.80, 0.37);
    public static readonly Color HillFront = Theme.Rgb(0.27, 0.60, 0.24);
    public static readonly Color Red = Theme.Rgb(0.85, 0.19, 0.17);
    public static readonly Color Blue = Theme.Rgb(0.17, 0.43, 0.82);
    public static readonly Color Word = Theme.Rgb(0.16, 0.34, 0.62);
    public static readonly Color Orange = Theme.Rgb(0.93, 0.45, 0.13);
    public static readonly Color Gold = Theme.Rgb(0.92, 0.68, 0.14);
    public static readonly Color Teal = Theme.Rgb(0.08, 0.57, 0.62);
    public static readonly Color Purple = Theme.Rgb(0.50, 0.32, 0.78);
    public static readonly Color Green = Theme.Rgb(0.22, 0.64, 0.33);
    public static readonly Color Slate = Theme.Rgb(0.42, 0.45, 0.50);
    public static readonly Color InkColor = Theme.Rgb(0.20, 0.21, 0.24);
}

internal sealed class Painter
{
    private readonly DrawingContext dc;
    private const double RimWidth = 3.6;

    public Painter(DrawingContext dc)
    {
        this.dc = dc;
    }

    // MARK: Primitives

    private static Geometry Rr(double x, double y, double w, double h, double r = 0)
    {
        var g = r > 0
            ? new RectangleGeometry(new Rect(x, y, w, h), Math.Min(r, w / 2), Math.Min(r, h / 2))
            : new RectangleGeometry(new Rect(x, y, w, h));
        g.Freeze();
        return g;
    }

    private static Geometry Poly(params (double x, double y)[] pts) => Geo.Poly(pts.Select(p => new Point(p.x, p.y)).ToList());

    private static Geometry Oval(double cx, double cy, double rx, double ry)
    {
        var g = new EllipseGeometry(new Point(cx, cy), rx, ry);
        g.Freeze();
        return g;
    }

    /// <summary>Points along a circle; angles in radians, counter-clockwise in this y-up space unless <paramref name="clockwise"/>.</summary>
    private static List<Point> ArcPoints(double cx, double cy, double r, double a0, double a1, bool clockwise)
    {
        if (!clockwise && a1 < a0) a1 += Math.PI * 2;
        if (clockwise && a1 > a0) a1 -= Math.PI * 2;
        int steps = Math.Max(8, (int)(Math.Abs(a1 - a0) * r / 2));
        var pts = new List<Point>();
        for (int i = 0; i <= steps; i++)
        {
            double a = a0 + (a1 - a0) * i / steps;
            pts.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return pts;
    }

    private static Geometry Stroked(Geometry path, double width, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(Brushes.Black, width) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        var g = path.GetWidenedPathGeometry(pen);
        g.Freeze();
        return g;
    }

    /// <summary>Fill (top→bottom gradient) then the dark rim.</summary>
    private void Body(Geometry path, (Color top, Color bottom) colors, double? rim = null)
    {
        Rect box = path.Bounds;
        var brush = new LinearGradientBrush(colors.top, colors.bottom, new Point(0, box.Bottom), new Point(0, box.Top))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        dc.DrawGeometry(brush, null, path);
        Outline(path, rim ?? RimWidth);
    }

    private void Solid(Geometry path, Color color, double? rim = null) =>
        Body(path, (color.Mix(Colors.White, 0.18), color.Mix(Colors.Black, 0.12)), rim);

    private void Fill(Geometry path, Color color) => dc.DrawGeometry(Theme.MakeBrush(color), null, path);

    private void Outline(Geometry path, double width)
    {
        if (width <= 0) return;
        dc.DrawGeometry(null, Theme.MakePen(Ink.Rim, width, PenLineCap.Round, PenLineJoin.Round), path);
    }

    private void Line((double x, double y)[] pts, Color? color = null, double width = 3, DashStyle? dash = null)
    {
        var pen = new Pen(Theme.MakeBrush(color ?? Ink.Rim), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (dash != null)
        {
            pen.DashStyle = dash;
            pen.DashCap = PenLineCap.Flat;
        }
        dc.DrawGeometry(null, pen, Geo.Line(pts.Select(p => new Point(p.x, p.y)).ToList()));
    }

    private void Clipped(Geometry path, Action body)
    {
        dc.PushClip(path);
        body();
        dc.Pop();
    }

    /// <summary>Text, un-flipped so it reads the right way up in this y-up space.</summary>
    private void Label(string s, double x, double y, double size, Color? color = null, Typeface? face = null)
    {
        dc.PushTransform(new ScaleTransform(1, -1, x, y));
        AgentWheel.Draw.Text(dc, s, new TextStyle(face ?? Fonts.Badge, size) { Color = color ?? Colors.White, Shadow = 0 }, new Point(x, y));
        dc.Pop();
    }

    private void Push(double tx, double ty, double radians)
    {
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(radians * 180 / Math.PI));
        group.Children.Add(new TranslateTransform(tx, ty));
        dc.PushTransform(group);
    }

    // MARK: Building blocks

    /// <summary>A sheet of paper with a folded top-right corner; <paramref name="inside"/> draws clipped to the sheet.</summary>
    private void Page(double x, double y, double w, double h, double fold = 17, Action? inside = null)
    {
        var sheet = Poly((x, y), (x, y + h), (x + w - fold, y + h), (x + w, y + h - fold), (x + w, y));
        Body(sheet, Ink.Paper, 0);
        if (inside != null) Clipped(sheet, inside);
        Outline(sheet, RimWidth);
        var flap = Poly((x + w - fold, y + h), (x + w - fold, y + h - fold), (x + w, y + h - fold));
        Body(flap, Ink.ShadowMetal);
    }

    /// <summary>Blue sky, sun and two green hills.</summary>
    private void Landscape(double x, double y, double w, double h, bool gray = false)
    {
        var frame = Rr(x, y, w, h, 1.5);
        Color Tone(Color c)
        {
            if (!gray) return c;
            double l = (0.3 * c.R + 0.59 * c.G + 0.11 * c.B) / 255.0;
            return Theme.White(l);
        }
        Clipped(frame, () =>
        {
            var sky = new LinearGradientBrush(Tone(Ink.Sky.Item1), Tone(Ink.Sky.Item2), new Point(0, y + h), new Point(0, y))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            dc.DrawRectangle(sky, null, new Rect(x - 2, y - 2, w + 4, h + 4));
            Fill(Oval(x + w * 0.74, y + h * 0.72, h * 0.13, h * 0.13), Tone(Ink.Sun));
            Fill(Poly((x - 2, y), (x - 2, y + h * 0.35), (x + w * 0.32, y + h * 0.66), (x + w * 0.62, y + h * 0.3), (x + w + 2, y + h * 0.2), (x + w + 2, y)), Tone(Ink.HillBack));
            Fill(Poly((x - 2, y), (x - 2, y + h * 0.12), (x + w * 0.5, y + h * 0.4), (x + w + 2, y + h * 0.1), (x + w + 2, y)), Tone(Ink.HillFront));
        });
        Outline(frame, 2.6);
    }

    private void Badge(double x, double y, double w, double h, Color color, string text, double? size = null)
    {
        Solid(Rr(x, y, w, h, 3), color, 3);
        Label(text, x + w / 2, y + h / 2, size ?? h * 0.74);
    }

    private void TextLines(double x, double y, double w, int rows, double gap = 8, bool boldFirst = false)
    {
        for (int i = 0; i < rows; i++)
        {
            double width = i == rows - 1 ? w * 0.6 : w;
            bool bold = i == 0 && boldFirst;
            Line(new[] { (x, y - i * gap), (x + width, y - i * gap) }, bold ? Ink.InkColor : Ink.Slate.WithAlpha(0.8), bold ? 4.2 : 3);
        }
    }

    private void Checker(double x, double y, double w, double h, double cell = 6)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(x, y, w, h));
        var gray = Theme.MakeBrush(Theme.White(0.62));
        int row = 0;
        for (double cy = y; cy < y + h; cy += cell, row++)
        {
            for (double cx = x + (row % 2 == 0 ? 0 : cell); cx < x + w; cx += cell * 2)
                dc.DrawRectangle(gray, null, new Rect(cx, cy, cell, cell));
        }
    }

    private void Browser(double x, double y, double w, double h, Action inside)
    {
        var window = Rr(x, y, w, h, 6);
        Body(window, Ink.Paper, 0);
        Clipped(window, () =>
        {
            dc.DrawRectangle(Theme.MakeBrush(Ink.ShadowMetal.Item1), null, new Rect(x, y + h - 15, w, 15));
            inside();
        });
        Line(new[] { (x, y + h - 15), (x + w, y + h - 15) }, null, 2.6);
        for (int k = 0; k < 3; k++) Fill(Oval(x + 9 + k * 8, y + h - 7.5, 2.6, 2.6), Ink.Rim);
        Outline(window, RimWidth);
    }

    private void Globe(double cx, double cy, double r)
    {
        Body(Oval(cx, cy, r, r), (Theme.Rgb(0.88, 0.93, 0.98), Theme.Rgb(0.66, 0.77, 0.90)), 3);
        Clipped(Oval(cx, cy, r, r), () =>
        {
            Outline(Oval(cx, cy, r * 0.45, r), 2.2);
            Outline(Oval(cx, cy, r, r * 0.42), 2.2);
            Line(new[] { (cx - r, cy), (cx + r, cy) }, null, 2.2);
            Line(new[] { (cx, cy - r), (cx, cy + r) }, null, 2.2);
        });
        Outline(Oval(cx, cy, r, r), 3);
    }

    private void Play(double cx, double cy, double s, Color? color = null)
    {
        var tri = Poly((cx - s * 0.42, cy - s * 0.5), (cx - s * 0.42, cy + s * 0.5), (cx + s * 0.52, cy));
        Solid(tri, color ?? Colors.White, 2.8);
    }

    /// <summary>A film strip with perforated edges; returns the picture window.</summary>
    private Rect Film(double x, double y, double w, double h)
    {
        Body(Rr(x, y, w, h, 5), Ink.Dark);
        var hole = Theme.MakeBrush(Theme.White(0.9));
        foreach (double side in new[] { x + 3, x + w - 11 })
        {
            for (double hy = y + 6; hy + 6 < y + h - 3; hy += 10)
                dc.DrawGeometry(hole, null, Rr(side, hy, 8, 6, 1.5));
        }
        var window = new Rect(x + 15, y + 6, w - 30, h - 12);
        Body(Rr(window.X, window.Y, window.Width, window.Height, 3), (Theme.Rgb(0.42, 0.45, 0.50), Theme.Rgb(0.22, 0.24, 0.27)), 2.6);
        return window;
    }

    private void Arrow(Point a, Point b, double shaft = 8, double head = 18, Color? color = null)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;
        var neck = new Point(b.X - ux * head, b.Y - uy * head);
        var p = Poly(
            (a.X + nx * shaft / 2, a.Y + ny * shaft / 2), (neck.X + nx * shaft / 2, neck.Y + ny * shaft / 2),
            (neck.X + nx * head * 0.62, neck.Y + ny * head * 0.62), (b.X, b.Y),
            (neck.X - nx * head * 0.62, neck.Y - ny * head * 0.62), (neck.X - nx * shaft / 2, neck.Y - ny * shaft / 2),
            (a.X - nx * shaft / 2, a.Y - ny * shaft / 2));
        Solid(p, color ?? Colors.White, 3);
    }

    private void Frame(double x, double y, double w, double h, bool gray = false)
    {
        Body(Rr(x, y, w, h, 4), Ink.Metal);
        Landscape(x + 6, y + 6, w - 12, h - 12, gray);
    }

    private void WaveBars(double x, double y, double w, double h, Color color)
    {
        double[] heights = { 0.25, 0.55, 0.85, 0.45, 1, 0.65, 0.3, 0.75, 0.4 };
        double step = w / heights.Length;
        var brush = Theme.MakeBrush(color);
        for (int i = 0; i < heights.Length; i++)
        {
            double bh = h * heights[i];
            dc.DrawGeometry(brush, null, Rr(x + i * step + step * 0.22, y + (h - bh) / 2, step * 0.56, bh, step * 0.28));
        }
    }

    // MARK: Glyphs

    public void Paint(Glyph glyph)
    {
        switch (glyph.Kind)
        {
            case GlyphKind.Png:
                Page(18, 6, 62, 88, 17, () =>
                {
                    Checker(62, 6, 18, 74);
                    Line(new[] { (62.0, 6.0), (62.0, 78.0) }, null, 2.4);
                });
                Landscape(25, 34, 33, 30);
                break;

            case GlyphKind.Jpg:
                Frame(8, 16, 84, 68);
                break;

            case GlyphKind.Heic:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 76, 30, 2));
                Badge(11, 26, 78, 26, Ink.Blue, "HEIC");
                break;

            case GlyphKind.Pdf:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 76, 30, 2));
                Badge(11, 26, 78, 26, Ink.Red, "PDF");
                break;

            case GlyphKind.Webp:
                Browser(7, 14, 86, 72, () => Globe(50, 41, 20));
                break;

            case GlyphKind.Avif:
                Page(14, 10, 60, 86, 17, () => { });
                Landscape(21, 40, 34, 30);
                Arrow(new Point(62, 38), new Point(92, 6), 9, 20);
                break;

            case GlyphKind.Gif:
            case GlyphKind.Film:
            {
                var w = Film(6, 18, 88, 64);
                Play(w.X + w.Width / 2 + 2, w.Y + w.Height / 2, 26);
                break;
            }

            case GlyphKind.Tiff:
                Body(Rr(27, 52, 46, 40, 2), Ink.Paper);
                TextLines(34, 82, 32, 2, 8);
                Body(Rr(8, 26, 84, 38, 8), Ink.Metal);
                Line(new[] { (22.0, 56.0), (78.0, 56.0) }, null, 3);
                Fill(Oval(80, 40, 3.6, 3.6), Theme.Rgb(0.35, 0.85, 0.40));
                Outline(Oval(80, 40, 3.6, 3.6), 1.8);
                Body(Rr(20, 8, 60, 22, 2), Ink.Paper);
                TextLines(28, 22, 44, 2, 7);
                break;

            case GlyphKind.LongImage:
                Body(Rr(30, 2, 40, 96, 3), Ink.Paper);
                foreach (double y in new[] { 68.0, 37.0, 6.0 }) Landscape(35, y + 2, 30, 24);
                break;

            case GlyphKind.Word:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 78, 34, 3));
                Badge(11, 22, 78, 26, Ink.Word, glyph.Text, glyph.Text.Length > 3 ? 18 : 20);
                break;

            case GlyphKind.Text:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 76, 36, 7, 8.5));
                break;

            case GlyphKind.Markdown:
                Page(20, 6, 60, 88, 17, () => { });
                Solid(Rr(24, 28, 52, 34, 5), Ink.InkColor, 3);
                Label("M↓", 50, 45, 24, null, Fonts.Hud(HudWeight.Heavy));
                break;

            case GlyphKind.Rtf:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 78, 34, 3, 8, true));
                Badge(11, 22, 78, 26, Ink.Purple, "RTF");
                break;

            case GlyphKind.Html:
                Browser(7, 14, 86, 72, () => Label("</>", 50, 41, 34, Ink.Orange));
                break;

            case GlyphKind.Odt:
                Page(20, 6, 60, 88, 17, () => TextLines(29, 78, 34, 3));
                Badge(11, 22, 78, 26, Ink.Teal, "ODT");
                break;

            case GlyphKind.Clapper:
            {
                Body(Rr(10, 10, 80, 54, 5), Ink.Dark);
                Play(52, 37, 24);
                Push(10, 66, 0.18);
                var stick = Rr(0, 0, 80, 16, 3);
                Body(stick, Ink.Dark, 0);
                Clipped(stick, () =>
                {
                    for (int k = 0; k < 5; k++)
                    {
                        double sx = k * 18 + 4;
                        Fill(Poly((sx, 0), (sx + 9, 0), (sx + 17, 16), (sx + 8, 16)), Colors.White);
                    }
                });
                Outline(stick, RimWidth);
                dc.Pop();
                Body(Rr(8, 62, 84, 6, 2), Ink.Dark);
                break;
            }

            case GlyphKind.FilmBadge:
                Film(6, 24, 88, 62);
                Badge(22, 8, 56, 24, Ink.Green, glyph.Text, 17);
                break;

            case GlyphKind.WebVideo:
                Browser(7, 14, 86, 72, () =>
                {
                    Solid(Rr(30, 26, 40, 28, 8), Ink.Red, 3);
                    Play(51, 40, 16);
                });
                break;

            case GlyphKind.Loop:
            {
                Body(Rr(8, 14, 84, 70, 6), Ink.Metal);
                Body(Rr(15, 21, 70, 56, 3), Ink.Dark, 2.6);
                var arc = Geo.Line(ArcPoints(50, 49, 18, 0.5, 0.5 + Math.PI * 1.55, false));
                var band = Stroked(arc, 7);
                Fill(band, Colors.White);
                Outline(band, 2.2);
                Arrow(new Point(66, 60), new Point(62, 72), 0.1, 14);
                Label("GIF", 50, 49, 14);
                break;
            }

            case GlyphKind.Note:
                Page(20, 10, 60, 84, 17, () => { });
                Label("♫", 48, 62, 46, Ink.InkColor, Fonts.Symbol);
                Badge(11, 16, 78, 26, glyph.Tint, glyph.Text);
                break;

            case GlyphKind.Waves:
                Body(Rr(6, 22, 88, 56, 8), Ink.Dark);
                WaveBars(14, 30, 72, 40, Theme.Rgb(0.55, 0.80, 1));
                break;

            case GlyphKind.WavesBadge:
                Body(Rr(6, 30, 88, 56, 8), Ink.Dark);
                WaveBars(14, 38, 72, 40, Theme.Rgb(0.55, 0.80, 1));
                Badge(24, 6, 52, 24, glyph.Tint, glyph.Text, 17);
                break;

            case GlyphKind.Voice:
            {
                // A speech bubble: rounded box plus a tail at the bottom left.
                var bubble = new CombinedGeometry(GeometryCombineMode.Union, Rr(6, 26, 88, 60, 14), Poly((22, 30), (13, 8), (40, 30)));
                bubble.Freeze();
                Body(bubble, Ink.Paper);
                WaveBars(16, 36, 68, 40, Ink.Teal);
                break;
            }

            case GlyphKind.Vinyl:
                Body(Oval(50, 50, 44, 44), Ink.Dark);
                foreach (double r in new[] { 36.0, 29.0, 22.0 }) Outline(Oval(50, 50, r, r), 1.2);
                dc.DrawGeometry(null, Theme.MakePen(Theme.White(1, 0.35), 3), Geo.Line(ArcPoints(50, 50, 32, 2.2, 2.9, false)));
                Solid(Oval(50, 50, 14, 14), Ink.Gold, 2.6);
                Body(Oval(50, 50, 3.5, 3.5), Ink.Dark, 1.6);
                break;

            case GlyphKind.Bell:
            {
                var bell = new StreamGeometry();
                using (var ctx = bell.Open())
                {
                    ctx.BeginFigure(new Point(16, 26), true, true);
                    ctx.LineTo(new Point(84, 26), true, true);
                    ctx.BezierTo(new Point(76, 32), new Point(72, 40), new Point(70, 52), true, true);
                    ctx.BezierTo(new Point(68, 92), new Point(32, 92), new Point(30, 52), true, true);
                    ctx.BezierTo(new Point(28, 40), new Point(24, 32), new Point(16, 26), true, true);
                }
                bell.Freeze();
                Body(Oval(50, 18, 9, 9), Ink.Metal);
                Body(bell, (Theme.Rgb(1, 0.88, 0.45), Theme.Rgb(0.88, 0.62, 0.12)));
                Body(Rr(46, 84, 8, 8, 3), Ink.Metal, 2.4);
                var wavePen = Theme.MakePen(Ink.Rim, 4, PenLineCap.Round, PenLineJoin.Round);
                dc.DrawGeometry(null, wavePen, Geo.Line(ArcPoints(50, 56, 44, Math.PI * 0.83, Math.PI * 1.07, false)));
                dc.DrawGeometry(null, wavePen, Geo.Line(ArcPoints(50, 56, 44, -Math.PI * 0.07, Math.PI * 0.17, false)));
                break;
            }

            case GlyphKind.Compress:
                Page(32, 12, 36, 76, 12, () => TextLines(38, 74, 22, 6, 9));
                Arrow(new Point(2, 50), new Point(30, 50), 9, 18);
                Arrow(new Point(98, 50), new Point(70, 50), 9, 18);
                break;

            case GlyphKind.Shrink:
                Frame(4, 30, 64, 52);
                Body(Oval(70, 38, 17, 17), (Theme.Rgb(0.90, 0.95, 1, 0.95), Theme.Rgb(0.68, 0.80, 0.92, 0.95)));
                Line(new[] { (62.0, 38.0), (78.0, 38.0) }, null, 5);
                Body(Poly((80, 25), (84, 21), (97, 8), (93, 4), (80, 17), (76, 21)), Ink.Dark);
                break;

            case GlyphKind.Cutout:
            {
                var card = Rr(8, 8, 84, 84, 6);
                Clipped(card, () => Checker(8, 8, 84, 84, 8));
                Outline(card, RimWidth);
                Body(Oval(50, 60, 15, 16), (Theme.Rgb(1, 0.86, 0.72), Theme.Rgb(0.93, 0.70, 0.55)));
                var torso = new StreamGeometry();
                using (var ctx = torso.Open())
                {
                    ctx.BeginFigure(new Point(18, 8), true, true);
                    ctx.BezierTo(new Point(20, 52), new Point(80, 52), new Point(82, 8), true, true);
                }
                torso.Freeze();
                Body(torso, (Theme.Rgb(0.35, 0.55, 0.92), Theme.Rgb(0.18, 0.32, 0.70)));
                var dash = new Pen(Brushes.White, 2.6) { DashStyle = new DashStyle(new[] { 5 / 2.6, 4 / 2.6 }, 0) };
                dc.DrawGeometry(null, dash, Oval(50, 60, 21, 22));
                break;
            }

            case GlyphKind.Scan:
                Page(24, 10, 52, 80, 14, () => { });
                Label("T", 50, 48, 46, Ink.InkColor, Fonts.Serif);
                foreach (var (cx, cy, sx, sy) in new[] { (10.0, 92.0, 1.0, -1.0), (90.0, 92.0, -1.0, -1.0), (10.0, 8.0, 1.0, 1.0), (90.0, 8.0, -1.0, 1.0) })
                    Line(new[] { (cx, cy + sy * 16), (cx, cy), (cx + sx * 16, cy) }, Ink.Blue, 6);
                break;

            case GlyphKind.Mono:
                Body(Rr(8, 16, 84, 68, 4), Ink.Metal);
                Landscape(14, 22, 72, 56);
                Clipped(Rr(14, 22, 36, 56), () => Landscape(14, 22, 72, 56, true));
                Line(new[] { (50.0, 22.0), (50.0, 78.0) }, null, 2.6);
                break;

            case GlyphKind.Rotate:
            {
                Push(46, 46, -0.26);
                Frame(-34, -26, 68, 54);
                dc.Pop();
                var arc = Geo.Line(ArcPoints(50, 50, 44, 1.75, 0.55, true));
                Solid(Stroked(arc, 7), Colors.White, 2.6);
                Arrow(new Point(84, 76), new Point(92, 62), 0.1, 15);
                break;
            }

            case GlyphKind.Unpin:
            {
                var pts = new List<Point>();
                var pin = new StreamGeometry();
                using (var ctx = pin.Open())
                {
                    ctx.BeginFigure(new Point(50, 4), true, true);
                    ctx.BezierTo(new Point(38, 24), new Point(22, 40), new Point(22, 62), true, true);
                    ctx.PolyLineTo(ArcPoints(50, 62, 28, Math.PI, 0, true), true, true);
                    ctx.BezierTo(new Point(78, 40), new Point(62, 24), new Point(50, 4), true, true);
                }
                pin.Freeze();
                Solid(pin, Ink.Red);
                Body(Oval(50, 62, 11, 11), Ink.Paper, 2.8);
                Solid(Poly((10, 14), (16, 8), (92, 84), (86, 90)), Colors.White, 3);
                break;
            }

            case GlyphKind.Backdrop:
            {
                var card = Rr(4, 4, 92, 92, 14);
                var grad = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(4, 96),
                    EndPoint = new Point(96, 4),
                };
                grad.GradientStops.Add(new GradientStop(Theme.Rgb(0.98, 0.36, 0.52), 0));
                grad.GradientStops.Add(new GradientStop(Theme.Rgb(1, 0.62, 0.36), 0.55));
                grad.GradientStops.Add(new GradientStop(Theme.Rgb(1, 0.82, 0.42), 1));
                grad.Freeze();
                dc.DrawGeometry(grad, null, card);
                Outline(card, RimWidth);
                dc.DrawGeometry(Theme.MakeBrush(Theme.White(0, 0.25)), null, Rr(19, 18, 64, 54, 6));
                Body(Rr(18, 22, 64, 54, 6), Ink.Paper, 2.6);
                Landscape(23, 27, 54, 44);
                break;
            }

            case GlyphKind.Split:
                Page(22, 6, 56, 88, 17, () => TextLines(30, 80, 32, 3));
                Line(new[] { (30.0, 46.0), (94.0, 46.0) }, Ink.Red, 4, new DashStyle(new[] { 7 / 4.0, 5 / 4.0 }, 0));
                Label("✂", 15, 46, 30, Ink.InkColor, Fonts.Symbol);
                break;

            case GlyphKind.Merge:
                Page(36, 22, 50, 72, 14, () => TextLines(43, 80, 30, 3));
                Page(14, 6, 50, 72, 14, () => TextLines(21, 64, 30, 4));
                Solid(Oval(74, 22, 16, 16), Ink.Green, 3);
                Label("+", 74, 23, 30);
                break;

            case GlyphKind.Mute:
                Body(Poly((10, 36), (30, 36), (52, 16), (52, 84), (30, 64), (10, 64)), Ink.Metal);
                Line(new[] { (62.0, 34.0), (90.0, 66.0) }, Ink.Red, 8);
                Line(new[] { (90.0, 34.0), (62.0, 66.0) }, Ink.Red, 8);
                break;

            case GlyphKind.Screen720:
                Body(Rr(30, 6, 40, 10, 3), Ink.Metal, 3);
                Body(Rr(44, 14, 12, 12), Ink.ShadowMetal, 2.6);
                Body(Rr(4, 24, 92, 64, 6), Ink.Dark);
                Label("720P", 50, 56, 30);
                break;

            case GlyphKind.Cover:
                Film(4, 36, 62, 58);
                Frame(34, 6, 62, 50);
                break;

            case GlyphKind.Fast:
                Solid(Poly((8, 16), (52, 50), (8, 84)), Ink.Gold);
                Solid(Poly((48, 16), (92, 50), (48, 84)), Ink.Gold);
                break;

            case GlyphKind.Slow:
            {
                Body(Rr(20, 86, 60, 8, 3), Ink.Dark);
                Body(Rr(20, 6, 60, 8, 3), Ink.Dark);
                var glass = Poly((26, 86), (74, 86), (54, 50), (74, 14), (26, 14), (46, 50));
                Body(glass, (Theme.Rgb(0.92, 0.96, 1), Theme.Rgb(0.75, 0.84, 0.94)), 0);
                Solid(Poly((36, 76), (64, 76), (50, 56)), Ink.Gold, 0);
                Solid(Poly((32, 18), (68, 18), (50, 36)), Ink.Gold, 0);
                Outline(glass, RimWidth);
                break;
            }

            case GlyphKind.Speaker:
                Body(Poly((8, 36), (28, 36), (50, 16), (50, 84), (28, 64), (8, 64)), Ink.Metal);
                dc.DrawGeometry(null, Theme.MakePen(Ink.Rim, 6, PenLineCap.Round, PenLineJoin.Round), Geo.Line(ArcPoints(52, 50, 22, -0.8, 0.8, false)));
                Label("1", 84, 50, 30);
                break;

            case GlyphKind.Meter:
            {
                Body(Rr(6, 8, 88, 84, 8), Ink.Dark);
                var colors = new[] { Ink.Green, Ink.Green, Ink.Gold, Ink.Orange, Ink.Red };
                for (int i = 0; i < 5; i++) Solid(Rr(16 + i * 15, 16, 10, 14 + i * 13, 2), colors[i], 2);
                Line(new[] { (14.0, 72.0), (86.0, 72.0) }, Theme.White(1, 0.8), 2.4);
                break;
            }

            case GlyphKind.Counter:
                Body(Rr(6, 18, 88, 64, 8), Ink.Paper);
                Label("123", 50, 50, 42, Ink.InkColor);
                break;

            case GlyphKind.Clipboard:
                Body(Rr(16, 4, 68, 86, 6), (Theme.Rgb(0.85, 0.66, 0.42), Theme.Rgb(0.66, 0.46, 0.26)));
                Body(Rr(24, 10, 52, 68, 2), Ink.Paper, 2.6);
                TextLines(31, 66, 38, 5, 10);
                Body(Rr(36, 80, 28, 14, 4), Ink.Metal, 3);
                break;

            case GlyphKind.Zip:
            {
                Body(Poly((6, 12), (6, 82), (36, 82), (44, 74), (94, 74), (94, 12)), (Theme.Rgb(1, 0.86, 0.45), Theme.Rgb(0.90, 0.66, 0.20)));
                for (int k = 0; k < 7; k++)
                {
                    double y = 66 - k * 8;
                    Body(Rr(k % 2 == 0 ? 44 : 50, y, 6, 5, 1), Ink.Dark, 1.4);
                }
                Body(Rr(42, 14, 16, 12, 3), Ink.Metal, 2.6);
                break;
            }

            case GlyphKind.Link:
                foreach (var (cx, cy) in new[] { (36.0, 36.0), (64.0, 64.0) })
                {
                    Push(cx, cy, Math.PI / 4);
                    Body(Stroked(Rr(-26, -12, 52, 24, 12), 9, PenLineCap.Round), Ink.Metal, 2.6);
                    dc.Pop();
                }
                break;

            case GlyphKind.Tag:
                Body(Poly((6, 34), (30, 10), (94, 10), (94, 58), (30, 58)), Ink.Paper);
                Outline(Oval(24, 34, 5, 5), 3);
                Label("Aa", 62, 34, 32, Ink.InkColor);
                break;

            case GlyphKind.Fingerprint:
            {
                var shield = new StreamGeometry();
                using (var ctx = shield.Open())
                {
                    ctx.BeginFigure(new Point(50, 96), true, true);
                    ctx.BezierTo(new Point(36, 88), new Point(24, 84), new Point(12, 80), true, true);
                    ctx.BezierTo(new Point(10, 40), new Point(24, 18), new Point(50, 4), true, true);
                    ctx.BezierTo(new Point(76, 18), new Point(90, 40), new Point(88, 80), true, true);
                    ctx.BezierTo(new Point(76, 84), new Point(64, 88), new Point(50, 96), true, true);
                }
                shield.Freeze();
                Body(shield, Ink.Metal);
                Label("#", 50, 50, 52, Ink.Blue);
                break;
            }
        }
    }
}
