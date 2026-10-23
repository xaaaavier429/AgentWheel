using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AgentWheel;

public sealed class TextStyle
{
    public Typeface Face;
    public double Size;
    public Color Color = Colors.White;
    /// <summary>Width of the dark rim drawn outside the glyphs.</summary>
    public double Outline;
    public Color OutlineColor = Theme.White(0, 0.82);
    public double Shadow = 3;
    /// <summary>Extra space between letters.</summary>
    public double Kern;
    public TextAlignment Align = TextAlignment.Center;
    public bool Wraps;

    public TextStyle(Typeface face, double size)
    {
        Face = face;
        Size = size;
    }
}

/// <summary>Text drawing in the HUD style: optional dark rim, soft shadow, truncation or wrapping.</summary>
public static class Draw
{
    /// <summary>Device pixels per WPF unit for the surface being drawn (set by each element before drawing).</summary>
    [ThreadStatic] public static double PixelsPerDip;

    private static double Ppd => PixelsPerDip > 0 ? PixelsPerDip : 1.0;

    public static FormattedText Make(string text, TextStyle st, double? maxWidth = null)
    {
        var ft = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, st.Face,
            Math.Max(1, st.Size), Theme.MakeBrush(st.Color), Ppd);
        if (maxWidth is double w && w > 1)
        {
            ft.MaxTextWidth = w;
            if (!st.Wraps)
            {
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
        }
        return ft;
    }

    public static Size Measure(string text, TextStyle st, double? maxWidth = null)
    {
        if (st.Kern > 0 && !st.Wraps) return new Size(KernedWidth(text, st), Make(text, st).Height);
        var ft = Make(text, st, maxWidth);
        return new Size(ft.Width, ft.Height);
    }

    private static double KernedWidth(string text, TextStyle st)
    {
        var chars = Elements(text);
        if (chars.Count == 0) return 0;
        return chars.Sum(c => Make(c, st).WidthIncludingTrailingWhitespace) + st.Kern * (chars.Count - 1);
    }

    private static List<string> Elements(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text ?? "");
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>Draws <paramref name="text"/> centred on <paramref name="center"/>, truncated (or wrapped) to <paramref name="maxWidth"/>.</summary>
    public static Rect Text(DrawingContext dc, string text, TextStyle st, Point center, double? maxWidth = null)
    {
        if (string.IsNullOrEmpty(text)) return Rect.Empty;
        if (st.Kern > 0 && !st.Wraps)
        {
            var parts = Elements(text);
            double total = KernedWidth(text, st);
            double h = Make(text, st).Height;
            double x = center.X - total / 2;
            double y = center.Y - h / 2;
            foreach (var part in parts)
            {
                var ft1 = Make(part, st);
                Paint(dc, ft1, new Point(x, y), st);
                x += ft1.WidthIncludingTrailingWhitespace + st.Kern;
            }
            return new Rect(center.X - total / 2, y, total, h);
        }
        var ft = Make(text, st, maxWidth);
        Point origin;
        double width;
        if (st.Wraps && maxWidth is double mw)
        {
            ft.TextAlignment = TextAlignment.Center;
            origin = new Point(center.X - mw / 2, center.Y - ft.Height / 2);
            width = mw;
        }
        else
        {
            origin = new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2);
            width = ft.Width;
        }
        Paint(dc, ft, origin, st);
        return new Rect(origin.X, origin.Y, width, ft.Height);
    }

    /// <summary>Draws text inside <paramref name="r"/>: top-aligned, horizontally per the style's alignment.</summary>
    public static void TextIn(DrawingContext dc, string text, TextStyle st, Rect r)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = Make(text, st, r.Width);
        double x = st.Align switch
        {
            TextAlignment.Right => r.Right - ft.Width,
            TextAlignment.Center => r.X + (r.Width - ft.Width) / 2,
            _ => r.X,
        };
        Paint(dc, ft, new Point(x, r.Y), st);
    }

    private static void Paint(DrawingContext dc, FormattedText ft, Point origin, TextStyle st)
    {
        if (st.Outline <= 0 && st.Shadow <= 0)
        {
            dc.DrawText(ft, origin);
            return;
        }
        Geometry geo = ft.BuildGeometry(origin);
        if (st.Shadow > 0)
        {
            dc.PushTransform(new TranslateTransform(0, Math.Max(0.8, st.Shadow * 0.35)));
            dc.DrawGeometry(Theme.MakeBrush(Theme.White(0, 0.42)), null, geo);
            dc.Pop();
        }
        if (st.Outline > 0)
        {
            var pen = new Pen(Theme.MakeBrush(st.OutlineColor), st.Outline * 2) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, geo);
            dc.DrawGeometry(Theme.MakeBrush(st.Color), null, geo);
        }
        else
        {
            dc.DrawText(ft, origin);
        }
    }
}

/// <summary>Wheel geometry. Angles are measured clockwise from 12 o'clock; WPF's y axis points down.</summary>
public static class Geo
{
    public static Point P(Point c, double r, double t) => new Point(c.X + r * Math.Sin(t), c.Y - r * Math.Cos(t));

    public static StreamGeometry Poly(IList<Point> pts, bool closed = true)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, closed);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    public static StreamGeometry Line(IList<Point> pts) => Poly(pts, false);

    /// <summary>One flat-edged slot of an octagonal ring; r0 / r1 are apothems (centre to the flat side).</summary>
    public static Geometry Facet(Point c, double r0, double r1, double t0, double t1)
    {
        double k = 1 / Math.Cos((t1 - t0) / 2);
        return Poly(new[] { P(c, r0 * k, t0), P(c, r1 * k, t0), P(c, r1 * k, t1), P(c, r0 * k, t1) });
    }

    /// <summary>Regular octagon whose flat sides face the eight slot directions.</summary>
    public static Geometry Octagon(Point c, double apothem)
    {
        double step = Math.PI / 4;
        double k = apothem / Math.Cos(step / 2);
        var pts = new List<Point>();
        for (int i = 0; i < 8; i++) pts.Add(P(c, k, i * step + step / 2));
        return Poly(pts);
    }

    public static Geometry Ring(Geometry outer, Geometry inner)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(outer);
        g.Children.Add(inner);
        g.Freeze();
        return g;
    }

    /// <summary>Tall diamond, like the red "revealed" marker over an enemy.</summary>
    public static Geometry Diamond(Point c, double half) => Poly(new[]
    {
        new Point(c.X, c.Y - half), new Point(c.X + half * 0.7, c.Y), new Point(c.X, c.Y + half), new Point(c.X - half * 0.7, c.Y),
    });
}

/// <summary>Valorant-style HUD panel: deep navy body, top-right corner cut off, agent-colour bar on the left.</summary>
public static class HudPanel
{
    public static void Fill(DrawingContext dc, Rect b, Color accent, double alpha = 0.86)
    {
        const double cut = 14;
        var shape = Geo.Poly(new[]
        {
            new Point(b.Left, b.Top), new Point(b.Right - cut, b.Top), new Point(b.Right, b.Top + cut),
            new Point(b.Right, b.Bottom), new Point(b.Left, b.Bottom),
        });
        dc.DrawGeometry(Theme.MakeBrush(Theme.Navy.WithAlpha(alpha)), Theme.MakePen(Theme.Bone.WithAlpha(0.14), 1), shape);
        dc.DrawRectangle(Theme.MakeBrush(accent), null, new Rect(b.Left, b.Top, 4, b.Height));
    }
}
