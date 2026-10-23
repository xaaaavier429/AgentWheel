using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentWheel;

/// <summary>The octagonal wheel: eight slots, dividers, highlight and the hub read-out.</summary>
public sealed class WheelVisual : FrameworkElement
{
    public WheelContext? Context;
    public int Page;
    public int? Hover;
    /// <summary>Animated highlight amount per slot (0…1).</summary>
    public double[] Glow = new double[8];
    public double Radius = 245;
    /// <summary>Fades slot contents in after a page flip.</summary>
    public double ContentFade = 1;
    /// <summary>Open animation, 0…1: the wheel grows into place.</summary>
    public double Pop = 1;
    public Color Accent = Theme.Accent;

    private const double Step = Math.PI / 4;

    public double Inner => Radius * 0.56;
    private double Scale => Radius / 245;

    public static double Side(double radius) => (radius / Math.Cos(Math.PI / 8) + 32) * 2;

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var c = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        double eased = 1 - Math.Pow(1 - Math.Clamp(Pop, 0, 1), 3);
        double k = 0.9 + 0.1 * eased;
        dc.PushTransform(new ScaleTransform(k, k, c.X, c.Y));
        DrawRing(dc, c);
        if (Context != null && Context.Pages.Count > 0)
        {
            var page = Context.Pages[Math.Clamp(Page, 0, Context.Pages.Count - 1)];
            DrawSlots(dc, c, page);
            DrawHub(dc, c, Context, page);
        }
        else
        {
            DrawPending(dc, c);
        }
        dc.Pop();
    }

    private void DrawRing(DrawingContext dc, Point c)
    {
        double R = Radius, r = Inner, s = Scale;
        double corner = 1 / Math.Cos(Step / 2);

        // Hub: a dark navy octagon so the read-out stays legible on any wallpaper.
        dc.DrawGeometry(Theme.MakeBrush(Theme.Navy.WithAlpha(0.62)), null, Geo.Octagon(c, r));

        // Octagonal ring body with a soft shadow so it floats above the desktop.
        var ring = Geo.Ring(Geo.Octagon(c, R), Geo.Octagon(c, r));
        dc.PushTransform(new TranslateTransform(0, 5 * s));
        dc.DrawGeometry(Theme.MakeBrush(Theme.White(0, 0.28)), null, ring);
        dc.Pop();
        dc.DrawGeometry(Theme.MakeBrush(Theme.Navy.WithAlpha(0.78)), null, ring);

        // Each slot gets a faint lift towards the rim; the highlighted one fills with the agent colour.
        for (int i = 0; i < 8; i++)
        {
            double t = i * Step;
            Point from = Geo.P(c, r, t), to = Geo.P(c, R, t);
            var facet = Geo.Facet(c, r, R, t - Step / 2, t + Step / 2);
            var lift = new LinearGradientBrush(Theme.White(1, 0.02), Theme.White(1, 0.08), from, to) { MappingMode = BrushMappingMode.Absolute };
            dc.DrawGeometry(lift, null, facet);
            double h = Glow[i];
            if (h > 0.002)
            {
                var lit = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
                lit.GradientStops.Add(new GradientStop(Accent.WithAlpha(0.16 * h), 0));
                lit.GradientStops.Add(new GradientStop(Accent.WithAlpha(0.5 * h), 0.6));
                lit.GradientStops.Add(new GradientStop(Accent.WithAlpha(0.88 * h), 1));
                dc.DrawGeometry(lit, null, facet);
            }
        }

        // Solid bar along the outer edge of the selected slot, and a thin one along its inner edge.
        double rim = Math.Max(4, 7 * s);
        for (int i = 0; i < 8; i++)
        {
            if (Glow[i] <= 0.002) continue;
            double t = i * Step;
            dc.DrawGeometry(Theme.MakeBrush(Accent.WithAlpha(Glow[i])), null, Geo.Facet(c, R - rim, R, t - Step / 2, t + Step / 2));
            dc.DrawGeometry(Theme.MakeBrush(Accent.WithAlpha(0.8 * Glow[i])), null, Geo.Facet(c, r, r + Math.Max(1.5, 2.5 * s), t - Step / 2, t + Step / 2));
        }

        // Thin bone-white dividers and edges.
        var divider = Theme.MakePen(Theme.Bone.WithAlpha(0.32), Math.Max(1, 1.2 * s));
        for (int i = 0; i < 8; i++)
        {
            double t = i * Step + Step / 2;
            dc.DrawLine(divider, Geo.P(c, r * corner, t), Geo.P(c, R * corner, t));
        }
        dc.DrawGeometry(null, Theme.MakePen(Theme.Bone.WithAlpha(0.55), Math.Max(1, 1.3 * s)), Geo.Octagon(c, R - 0.5));
        dc.DrawGeometry(null, Theme.MakePen(Theme.Bone.WithAlpha(0.3), 1), Geo.Octagon(c, r));
        // Small tactical ticks pointing out from each outer corner.
        var tick = Theme.MakePen(Theme.Bone.WithAlpha(0.5), Math.Max(1, 1.5 * s));
        for (int i = 0; i < 8; i++)
        {
            double t = i * Step + Step / 2;
            dc.DrawLine(tick, Geo.P(c, R * corner + 6 * s, t), Geo.P(c, R * corner + 13 * s, t));
        }
    }

    private void DrawSlots(DrawingContext dc, Point c, WheelPage page)
    {
        double s = Scale;
        double mid = (Inner + Radius) / 2;
        for (int i = 0; i < 8; i++)
        {
            var option = i < page.Slots.Count ? page.Slots[i] : null;
            if (option == null) continue;
            Point p = Geo.P(c, mid, i * Step);
            // The source's own format stays readable ("目前"); missing tools fade further.
            bool isCurrent = option.Ammo == "目前";
            dc.PushOpacity((option.Enabled ? 1 : (isCurrent ? 0.72 : 0.38)) * ContentFade);
            Glyph.Of(option.Id).Draw(dc, new Rect(p.X - 29 * s, p.Y - 56 * s, 58 * s, 58 * s));
            string name = option.Big ?? option.Title;
            Draw.Text(dc, name, new TextStyle(Fonts.Badge, (name.Length > 4 ? 19 : 23) * s) { Outline = 2 * s, Shadow = 4 },
                new Point(p.X, p.Y + 16 * s));
            if (option.Big != null || !option.Enabled)
            {
                Draw.Text(dc, option.Ammo, new TextStyle(Fonts.Hud(HudWeight.Bold), 13 * s)
                {
                    Outline = 1.4 * s,
                    OutlineColor = Theme.White(0, 0.7),
                    Shadow = 2,
                }, new Point(p.X, p.Y + 35 * s));
            }
            dc.Pop();
        }
    }

    private void DrawHub(DrawingContext dc, Point c, WheelContext context, WheelPage page)
    {
        double r = Inner, s = Scale;
        WheelOption? option = Hover is int h && h < page.Slots.Count ? page.Slots[h] : null;

        string kicker = Kinds.Title(context.Kind) + " · " + page.Name;
        Draw.Text(dc, kicker, new TextStyle(Fonts.Hud(HudWeight.Bold), 15 * s) { Color = Theme.Bone.WithAlpha(0.9), Shadow = 3 },
            new Point(c.X, c.Y - r * 0.5), r * 1.5);
        dc.DrawRectangle(Theme.MakeBrush(Accent), null, new Rect(c.X - 14 * s, c.Y - r * 0.5 + 13 * s, 28 * s, 2 * s));

        string title = option?.Title ?? context.Headline;
        double titleSize = option == null ? 23 : 30;
        Draw.Text(dc, title, new TextStyle(Fonts.Hud(HudWeight.Bold), titleSize * s)
        {
            Outline = 1.4 * s,
            OutlineColor = Theme.White(0, 0.5),
            Shadow = 6,
        }, new Point(c.X, c.Y - r * 0.21), r * 1.62);

        string detail = context.Summary;
        Color color = Theme.White(1, 0.86);
        if (option != null)
        {
            if (option.Enabled)
            {
                detail = option.Detail;
            }
            else
            {
                detail = option.Note ?? "現在用不了";
                color = Theme.Loss.Mix(Colors.White, 0.45);
            }
        }
        Draw.Text(dc, detail, new TextStyle(Fonts.Hud(HudWeight.Medium), 14.5 * s) { Color = color, Shadow = 3, Wraps = true },
            new Point(c.X, c.Y + r * 0.13), r * 1.42);

        if (context.Pages.Count > 1) DrawPager(dc, context.Pages.Count, new Point(c.X, c.Y + r * 0.5));
    }

    private void DrawPager(DrawingContext dc, int count, Point p)
    {
        double s = Scale;
        string label = (Page + 1) + " / " + count;
        var style = new TextStyle(Fonts.Hud(HudWeight.Demi), 18 * s) { Shadow = 3 };
        double width = Draw.Measure(label, style).Width;
        Draw.Text(dc, label, style, p);
        double gap = width / 2 + 15 * s;
        var pen = Theme.MakePen(Accent, 2.8 * s, PenLineCap.Round, PenLineJoin.Round);
        dc.DrawGeometry(null, pen, Geo.Line(new[] { new Point(p.X - gap + 4 * s, p.Y - 7 * s), new Point(p.X - gap - 3 * s, p.Y), new Point(p.X - gap + 4 * s, p.Y + 7 * s) }));
        dc.DrawGeometry(null, pen, Geo.Line(new[] { new Point(p.X + gap - 4 * s, p.Y - 7 * s), new Point(p.X + gap + 3 * s, p.Y), new Point(p.X + gap - 4 * s, p.Y + 7 * s) }));
    }

    /// <summary>Shown for a moment after Shift, until Windows tells us which files are being dragged.</summary>
    private void DrawPending(DrawingContext dc, Point c)
    {
        double r = Inner, s = Scale;
        Draw.Text(dc, "特戰輪盤", new TextStyle(Fonts.Hud(HudWeight.Bold), 15 * s) { Color = Theme.Bone.WithAlpha(0.9), Shadow = 3 },
            new Point(c.X, c.Y - r * 0.5), r * 1.5);
        dc.DrawRectangle(Theme.MakeBrush(Accent), null, new Rect(c.X - 14 * s, c.Y - r * 0.5 + 13 * s, 28 * s, 2 * s));
        Draw.Text(dc, "讀取檔案中…", new TextStyle(Fonts.Hud(HudWeight.Bold), 23 * s) { Shadow = 6 }, new Point(c.X, c.Y - r * 0.15), r * 1.6);
        Draw.Text(dc, "稍微移動一下滑鼠", new TextStyle(Fonts.Hud(HudWeight.Medium), 14.5 * s) { Color = Theme.White(1, 0.75), Shadow = 3 },
            new Point(c.X, c.Y + r * 0.15), r * 1.4);
    }
}

/// <summary>Top-right stat panel: white is what stays, teal is an upgrade, red is what you give up.</summary>
public sealed class StatsPanel : FrameworkElement
{
    public string Title = "";
    public string Caption = "";
    public string[] Labels = Array.Empty<string>();
    public double[] Base = Array.Empty<double>();
    public double[] Target = Array.Empty<double>();
    public double[] Shown = Array.Empty<double>();
    public Color Accent = Theme.Accent;

    public const double PanelWidth = 316;

    public static double PanelHeight(int rows) => 72 + rows * 27 + 12;

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var b = new Rect(RenderSize);
        HudPanel.Fill(dc, b, Accent);
        Draw.TextIn(dc, Title.ToUpperInvariant(), new TextStyle(Fonts.Hud(HudWeight.Bold), 20) { Color = Theme.Bone, Shadow = 0, Align = TextAlignment.Left },
            new Rect(20, 10, b.Width - 40, 28));
        Draw.TextIn(dc, Caption, new TextStyle(Fonts.Hud(HudWeight.Medium), 13) { Color = Theme.Bone.WithAlpha(0.6), Shadow = 0, Align = TextAlignment.Left },
            new Rect(20, 40, b.Width - 40, 20));
        dc.DrawRectangle(Theme.MakeBrush(Theme.Bone.WithAlpha(0.14)), null, new Rect(20, 62, b.Width - 36, 1));

        for (int i = 0; i < Labels.Length; i++)
        {
            double y = 72 + i * 27;
            Draw.TextIn(dc, Labels[i], new TextStyle(Fonts.Hud(HudWeight.Demi), 14) { Color = Theme.Bone.WithAlpha(0.92), Shadow = 0, Align = TextAlignment.Left },
                new Rect(20, y, 84, 20));
            var bar = new Rect(104, y + 6, b.Width - 120, 8);
            double start = i < Base.Length ? Base[i] : 0;
            DrawBar(dc, bar, start, i < Shown.Length ? Shown[i] : start);
        }
    }

    private static void DrawBar(DrawingContext dc, Rect r, double baseValue, double value)
    {
        dc.DrawRectangle(Theme.MakeBrush(Theme.Bone.WithAlpha(0.15)), null, r);
        double lo = Math.Min(baseValue, value), hi = Math.Max(baseValue, value);
        dc.DrawRectangle(Theme.MakeBrush(Theme.Bone), null, new Rect(r.X, r.Y, r.Width * lo, r.Height));
        if (hi - lo > 0.004)
            dc.DrawRectangle(Theme.MakeBrush(value > baseValue ? Theme.Gain : Theme.Loss), null, new Rect(r.X + r.Width * lo, r.Y, r.Width * (hi - lo), r.Height));
        var gap = Theme.MakeBrush(Theme.Navy);
        for (int k = 1; k < 5; k++) dc.DrawRectangle(gap, null, new Rect(r.X + r.Width * k / 5 - 1, r.Y, 2, r.Height));
    }
}

/// <summary>Top-left help box with key caps.</summary>
public sealed class HelpPanel : FrameworkElement
{
    public Color Accent = Theme.Accent;

    public static readonly Size PanelSize = new Size(290, 104);

    private static readonly (bool isKey, string text)[][] Lines =
    {
        new[] { (false, "按"), (true, "⇧ Shift"), (false, "切換  武器 / 技能") },
        new[] { (false, "朝格子方向拖，"), (true, "放開滑鼠"), (false, "執行") },
        new[] { (false, "移回中心再放開 = 取消") },
    };

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        HudPanel.Fill(dc, new Rect(RenderSize), Accent);
        var textStyle = new TextStyle(Fonts.Hud(HudWeight.Demi), 15) { Color = Theme.Bone, Shadow = 0, Align = TextAlignment.Left };
        for (int i = 0; i < Lines.Length; i++)
        {
            double x = 18;
            double y = 16 + i * 28;
            foreach (var (isKey, text) in Lines[i])
            {
                if (isKey)
                {
                    x += DrawKey(dc, text, new Point(x, y + 1)) + 4;
                }
                else
                {
                    double w = Draw.Measure(text, textStyle).Width;
                    Draw.TextIn(dc, text, textStyle, new Rect(x, y, w + 4, 22));
                    x += w + 4;
                }
            }
        }
    }

    private static double DrawKey(DrawingContext dc, string key, Point p)
    {
        var style = new TextStyle(Fonts.Hud(HudWeight.Bold), 13) { Color = Theme.Navy, Shadow = 0 };
        double w = Math.Max(22, Draw.Measure(key, style).Width + 12);
        var r = new Rect(p.X, p.Y, w, 21);
        dc.DrawRoundedRectangle(Theme.MakeBrush(Theme.Bone), null, r, 2, 2);
        Draw.Text(dc, key, style, new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        return w;
    }
}

/// <summary>Darkens the desktop with a vignette centred on the wheel.</summary>
public sealed class DimVisual : FrameworkElement
{
    public Point Spot;

    protected override void OnRender(DrawingContext dc)
    {
        var b = new Rect(RenderSize);
        dc.DrawRectangle(Theme.MakeBrush(Theme.Navy.WithAlpha(0.3)), null, b);
        double reach = Math.Sqrt(b.Width * b.Width + b.Height * b.Height) * 0.7;
        var vignette = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = Spot,
            GradientOrigin = Spot,
            RadiusX = reach,
            RadiusY = reach,
        };
        vignette.GradientStops.Add(new GradientStop(Theme.White(0, 0), 0.15));
        vignette.GradientStops.Add(new GradientStop(Theme.White(0, 0.5), 1));
        dc.DrawRectangle(vignette, null, b);
    }
}

/// <summary>Red "revealed" markers, top right: how much of the file can be traced back to you.</summary>
public sealed class WantedVisual : FrameworkElement
{
    public static readonly Size PanelSize = new Size(380, 70);

    public int Level { get; private set; }
    private List<string> reasons = new();
    /// <summary>0…1 while the markers light up one by one.</summary>
    public double Appear = 1;
    /// <summary>Seconds since shown; drives the pulse and the blink.</summary>
    public double Phase;
    /// <summary>Clearing the reveal: earned markers blink.</summary>
    public bool Evading;
    public string? Caption;

    public void Show(Wanted wanted)
    {
        Level = wanted.Level;
        reasons = wanted.Reasons.ToList();
        Appear = 0;
        Phase = 0;
        Evading = false;
        Caption = null;
        Visibility = Level == 0 ? Visibility.Hidden : Visibility.Visible;
        InvalidateVisual();
    }

    public void Reset() => Show(new Wanted());

    protected override void OnRender(DrawingContext dc)
    {
        if (Level <= 0) return;
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var b = new Rect(RenderSize);
        double radius = 15, gap = 5;
        double rowWidth = radius * 2 * 5 + gap * 4;
        var rowMid = new Point(b.Right - rowWidth / 2 - 4, radius + 6);

        // A red radar pulse behind the markers while you are revealed.
        if (!Evading)
        {
            double pulse = 0.32 + 0.22 * Math.Sin(Phase * 5);
            var glow = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = rowMid,
                GradientOrigin = rowMid,
                RadiusX = rowWidth * 0.36 * 2.6,
                RadiusY = rowWidth * 0.36,
            };
            glow.GradientStops.Add(new GradientStop(Theme.Red.WithAlpha(pulse), 0));
            glow.GradientStops.Add(new GradientStop(Theme.Red.WithAlpha(0), 1));
            dc.DrawRectangle(glow, null, b);
        }

        for (int i = 0; i < 5; i++)
        {
            // Markers fill right-to-left: the leftmost slot is the fifth one.
            int rank = 4 - i;
            var center = new Point(rowMid.X - rowWidth / 2 + radius + i * (radius * 2 + gap), rowMid.Y);
            bool earned = rank < Level;
            double start = rank * 0.16;
            double local = earned ? Math.Clamp((Appear - start) / 0.3, 0, 1) : 1;
            double pop = earned ? 1 + 0.45 * Math.Sin(local * Math.PI) * (1 - local) : 1;
            var path = Geo.Diamond(center, radius * pop);
            if (earned && local > 0)
            {
                bool lit = !Evading || ((int)(Phase * 7)) % 2 == 0;
                if (lit) dc.DrawGeometry(Theme.MakeBrush(Theme.Red.WithAlpha(0.35)), null, Geo.Diamond(center, radius * pop + 4));
                dc.DrawGeometry(Theme.MakeBrush(lit ? Theme.Red : Theme.White(0.55, 0.9)), Theme.MakePen(Theme.Navy.WithAlpha(0.9), 1.6), path);
            }
            else
            {
                dc.DrawGeometry(Theme.MakeBrush(Theme.Navy.WithAlpha(0.5)), Theme.MakePen(Theme.Bone.WithAlpha(0.3), 1), path);
            }
        }

        string text = Caption ?? string.Join(" · ", reasons.Take(3));
        if (text.Length == 0) return;
        var style = new TextStyle(Fonts.Hud(HudWeight.Demi), 13) { Color = Theme.White(1, 0.92), Shadow = 0, Align = TextAlignment.Right };
        double width = Math.Min(Draw.Measure(text, style).Width + 18, b.Width);
        var pill = new Rect(b.Right - width, b.Bottom - 22, width, 20);
        dc.DrawRectangle(Theme.MakeBrush(Theme.Navy.WithAlpha(0.85)), null, pill);
        dc.DrawRectangle(Theme.MakeBrush(Theme.Red), null, new Rect(pill.X, pill.Y, 3, pill.Height));
        Draw.TextIn(dc, text, style, new Rect(pill.X + 9, pill.Y + 1.5, pill.Width - 18, pill.Height - 3));
    }
}

/// <summary>Everything drawn over the desktop. Shared by the live overlay and --snapshot.</summary>
public sealed class OverlayContent : Canvas
{
    public readonly DimVisual Dim = new();
    public readonly WheelVisual Wheel = new();
    public readonly StatsPanel Stats = new();
    public readonly HelpPanel Help = new();
    public readonly WantedVisual Wanted = new();

    public OverlayContent()
    {
        foreach (UIElement child in new UIElement[] { Dim, Wheel, Stats, Help, Wanted }) Children.Add(child);
        Wanted.Visibility = Visibility.Hidden;
    }

    private static void Place(FrameworkElement e, Rect r)
    {
        SetLeft(e, r.X);
        SetTop(e, r.Y);
        e.Width = Math.Max(0, r.Width);
        e.Height = Math.Max(0, r.Height);
    }

    /// <summary>Lays out the panels for a wheel centred at <paramref name="center"/> on a surface of <paramref name="size"/>.</summary>
    public void Arrange(Size size, Point center, double radius, int rows)
    {
        Width = size.Width;
        Height = size.Height;
        Place(Dim, new Rect(size));
        Dim.Spot = center;
        double side = WheelVisual.Side(radius);
        Wheel.Radius = radius;
        Place(Wheel, new Rect(center.X - side / 2, center.Y - side / 2, side, side));

        const double margin = 28;
        double statsHeight = StatsPanel.PanelHeight(rows);
        var starsSize = WantedVisual.PanelSize;
        // Top-right stack: the stat panel, with the reveal markers underneath it.
        var stats = new Rect(size.Width - StatsPanel.PanelWidth - margin, margin, StatsPanel.PanelWidth, statsHeight);
        var stars = new Rect(size.Width - starsSize.Width - margin, stats.Bottom + 8, starsSize.Width, starsSize.Height);
        var help = new Rect(margin, margin, HelpPanel.PanelSize.Width, HelpPanel.PanelSize.Height);
        var ringRect = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        if (stats.IntersectsWith(ringRect) || stars.IntersectsWith(ringRect))
        {
            stars.Y = size.Height - margin - starsSize.Height;
            stats.Y = stars.Y - 8 - statsHeight;
        }
        if (help.IntersectsWith(ringRect)) help.Y = size.Height - margin - help.Height;
        Place(Stats, stats);
        Place(Help, help);
        Place(Wanted, stars);
        Dim.InvalidateVisual();
    }

    public void Apply(WheelContext? context, int page, int? hover)
    {
        Wheel.Context = context;
        Wheel.Page = page;
        Wheel.Hover = hover;
        if (context == null)
        {
            Stats.Visibility = Visibility.Hidden;
            Wheel.InvalidateVisual();
            return;
        }
        var slots = page < context.Pages.Count ? context.Pages[page].Slots : new List<WheelOption?>();
        WheelOption? option = hover is int h && h < slots.Count ? slots[h] : null;
        Stats.Labels = context.StatLabels;
        Stats.Base = context.Base;
        if (option != null)
        {
            Stats.Title = option.Title;
            Stats.Caption = option.Enabled ? context.Headline + "  →  " + option.Title : (option.Note ?? "");
            Stats.Target = option.Enabled ? (option.Stats ?? context.Base) : context.Base;
        }
        else
        {
            Stats.Title = context.Headline;
            Stats.Caption = context.Summary;
            Stats.Target = context.Base;
        }
        if (Stats.Shown.Length != Stats.Target.Length) Stats.Shown = context.Base.ToArray();
        Stats.Visibility = context.StatLabels.Length == 0 ? Visibility.Hidden : Visibility.Visible;
        Wheel.InvalidateVisual();
        Stats.InvalidateVisual();
    }

    public void SetAccent(Color color)
    {
        Wheel.Accent = color;
        Stats.Accent = color;
        Help.Accent = color;
    }
}
