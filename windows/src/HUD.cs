using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentWheel;

/// <summary>How a finished job is announced, borrowing Valorant's end-of-round callouts.</summary>
public enum Outcome
{
    victory,
    ace,
    thrifty,
    flawless,
    defeat,
}

public static class Outcomes
{
    public static string Word(this Outcome o) => o.ToString().ToUpperInvariant();

    public static string Kicker(this Outcome o) => o switch
    {
        Outcome.ace => "一口氣清空全部目標",
        Outcome.thrifty => "體積省下一大半",
        Outcome.flawless => "零暴露 · 完美回合",
        Outcome.defeat => "回合落敗",
        _ => "回合勝利",
    };

    public static Color Tint(this Outcome o) => o switch
    {
        Outcome.ace or Outcome.thrifty => Theme.Gold,
        Outcome.defeat => Theme.Wasted,
        _ => Theme.Victory,
    };

    public static SfxKind Sound(this Outcome o) => o switch
    {
        Outcome.ace => SfxKind.ace,
        Outcome.flawless => SfxKind.evaded,
        Outcome.defeat => SfxKind.wasted,
        _ => SfxKind.passed,
    };

    /// <summary>FLAWLESS for a privacy wipe, ACE for 5+ files, THRIFTY at half the size or less, VICTORY otherwise.</summary>
    public static Outcome Pick(int files, long before, long after, bool evaded)
    {
        if (evaded) return Outcome.flawless;
        if (files >= 5) return Outcome.ace;
        if (before > 0 && after > 0 && after * 2 <= before) return Outcome.thrifty;
        return Outcome.victory;
    }

    public static bool TryParse(string? word, out Outcome outcome) =>
        Enum.TryParse(word?.Trim().ToLowerInvariant(), out outcome);
}

/// <summary>A borderless, click-through, always-on-top window placed in physical pixels.</summary>
public sealed class HudWindow : Window
{
    public HudWindow(FrameworkElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        Content = content;
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true, true);
    }

    public void ShowAt(int x, int y, int width, int height)
    {
        Opacity = 1;
        if (!IsVisible) Show();
        Native.Place(new WindowInteropHelper(this).Handle, x, y, width, height);
    }
}

/// <summary>VICTORY / ACE / THRIFTY / FLAWLESS / DEFEAT splash.</summary>
public sealed class BannerVisual : FrameworkElement
{
    public const double BandHeight = 280;

    private Outcome outcome = Outcome.victory;
    private string title = "";
    private string detail = "";
    /// <summary>DEFEAT covers the whole screen and darkens it.</summary>
    public bool FullScreen;
    /// <summary>Intro animation, 0…1.</summary>
    public double Progress = 1;

    public void Configure(bool passed, string title, string detail, Outcome? outcome = null)
    {
        this.outcome = outcome ?? (passed ? Outcome.victory : Outcome.defeat);
        this.title = title;
        this.detail = detail;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double ease = 1 - Math.Pow(1 - Math.Clamp(Progress, 0, 1), 3);
        Color color = outcome.Tint();
        var bounds = new Rect(RenderSize);
        dc.PushOpacity(ease);
        if (FullScreen)
        {
            var mid = new Point(bounds.Width / 2, bounds.Height / 2);
            double reach = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2;
            var shade = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = mid,
                GradientOrigin = mid,
                RadiusX = reach,
                RadiusY = reach,
            };
            shade.GradientStops.Add(new GradientStop(Theme.Red.WithAlpha(0.12), 0));
            shade.GradientStops.Add(new GradientStop(Theme.White(0, 0.74), 1));
            dc.DrawRectangle(shade, null, bounds);
        }
        // The band and text sit in a strip of BandHeight, centred (a little high on the full-screen variant).
        Rect b = FullScreen ? new Rect(0, bounds.Height / 2 - 40 - BandHeight / 2, bounds.Width, BandHeight) : bounds;
        double midX = b.X + b.Width / 2, midY = b.Y + b.Height / 2;

        // Navy band that fades out towards both screen edges and softly at the top and bottom.
        var band = new Rect(0, midY - 122, b.Width, 244);
        var across = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(b.Width, 0) };
        across.GradientStops.Add(new GradientStop(Theme.Navy.WithAlpha(0), 0.06));
        across.GradientStops.Add(new GradientStop(Theme.Navy.WithAlpha(0.84), 0.3));
        across.GradientStops.Add(new GradientStop(Theme.Navy.WithAlpha(0.84), 0.7));
        across.GradientStops.Add(new GradientStop(Theme.Navy.WithAlpha(0), 0.94));
        var down = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, band.Top), EndPoint = new Point(0, band.Bottom) };
        down.GradientStops.Add(new GradientStop(Theme.White(0, 0), 0));
        down.GradientStops.Add(new GradientStop(Theme.White(0, 1), 0.16));
        down.GradientStops.Add(new GradientStop(Theme.White(0, 1), 0.84));
        down.GradientStops.Add(new GradientStop(Theme.White(0, 0), 1));
        dc.PushOpacityMask(down);
        dc.DrawRectangle(across, null, band);
        dc.Pop();

        // Two thin lines in the outcome colour, sliding out from the centre.
        double reachX = b.Width * 0.3 * ease;
        var lineBrush = Theme.MakeBrush(color.WithAlpha(0.85));
        dc.DrawRectangle(lineBrush, null, new Rect(midX - reachX, band.Top + 18, reachX * 2, 2));
        dc.DrawRectangle(lineBrush, null, new Rect(midX - reachX, band.Bottom - 20, reachX * 2, 2));

        // Small kicker, then the big word punching in from slightly larger.
        Draw.Text(dc, outcome.Kicker(), new TextStyle(Fonts.Hud(HudWeight.Bold), 16) { Color = color, Shadow = 3, Kern = 3 },
            new Point(midX + 1.5, midY - 88));
        double zoom = 1 + 0.22 * (1 - ease);
        double wordY = midY - 26;
        dc.PushTransform(new ScaleTransform(zoom, zoom, midX, wordY));
        Draw.Text(dc, outcome.Word(), new TextStyle(Fonts.Splash, outcome == Outcome.defeat ? 100 : 94) { Color = color, Shadow = 14, Kern = 10 },
            new Point(midX, wordY));
        dc.Pop();

        dc.DrawRectangle(Theme.MakeBrush(Theme.Bone.WithAlpha(0.45)), null, new Rect(midX - 230, midY + 30, 460, 1));
        Draw.Text(dc, title, new TextStyle(Fonts.Hud(HudWeight.Demi), 21) { Color = Theme.Bone, Shadow = 4 }, new Point(midX, midY + 48), b.Width * 0.7);
        Draw.Text(dc, detail, new TextStyle(Fonts.Hud(HudWeight.Medium), 16) { Color = Theme.Bone.WithAlpha(0.75), Shadow = 3 },
            new Point(midX, midY + 72), b.Width * 0.7);
        dc.Pop();
    }
}

public sealed class BannerController
{
    public static readonly BannerController Shared = new();

    private HudWindow? window;
    private readonly BannerVisual view = new();
    private DispatcherTimer? timer;
    private DateTime startedAt;
    private DispatcherTimer? hideTimer;

    public void Show(bool passed, string title, string detail, Native.MonitorInfo? monitor = null, Outcome? outcome = null)
    {
        // Failures always show; the success splash can be switched off in the menu.
        if (passed && !Settings.Banner) return;
        var m = monitor ?? Native.MonitorAt(Native.CursorPos);
        window ??= new HudWindow(view);
        view.FullScreen = !passed;
        view.Configure(passed, title, detail, outcome);
        view.Progress = 0;
        if (passed)
        {
            int h = (int)Math.Round(BannerVisual.BandHeight * m.Scale);
            int y = m.Bounds.Top + m.Bounds.Height / 2 - h / 2 - (int)Math.Round(40 * m.Scale);
            window.ShowAt(m.Bounds.Left, y, m.Bounds.Width, h);
        }
        else
        {
            window.ShowAt(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height);
        }

        startedAt = DateTime.Now;
        timer?.Stop();
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = (DateTime.Now - startedAt).TotalSeconds / 0.28;
            view.Progress = Math.Min(p, 1);
            view.InvalidateVisual();
            if (p >= 1) timer?.Stop();
        };
        timer.Start();

        hideTimer?.Stop();
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(passed ? 2.2 : 3.2) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer?.Stop();
            Fade.Out(window, 0.35);
        };
        hideTimer.Start();
    }
}

/// <summary>Fades a window out and hides it.</summary>
public static class Fade
{
    public static void Out(Window? window, double seconds)
    {
        if (window == null) return;
        var start = DateTime.Now;
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            double p = (DateTime.Now - start).TotalSeconds / seconds;
            if (p >= 1)
            {
                t.Stop();
                window.Hide();
                window.Opacity = 1;
                return;
            }
            window.Opacity = 1 - p;
        };
        t.Start();
    }
}

/// <summary>Bottom-right progress, styled as the spike-plant bar: a blinking spike light and a fill bar.</summary>
public sealed class BusyVisual : FrameworkElement
{
    public string Text = "";
    public double Progress;
    public double Angle;

    public static readonly Size PanelSize = new Size(520, 52);

    protected override void OnRender(DrawingContext dc)
    {
        Draw.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var b = new Rect(RenderSize);
        HudPanel.Fill(dc, b, Theme.Red, 0.88);
        double midY = b.Height / 2;

        // The spike's light blinks while it is being planted.
        double blink = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(Angle * 1.6));
        dc.DrawGeometry(Theme.MakeBrush(Theme.Red.WithAlpha(blink)), null, Geo.Diamond(new Point(26, midY - 4), 10));

        Draw.TextIn(dc, Text, new TextStyle(Fonts.Hud(HudWeight.Demi), 16) { Color = Theme.Bone, Shadow = 0, Align = TextAlignment.Left },
            new Rect(44, midY - 17, b.Width - 60, 22));

        // Plant bar: real progress when known, otherwise a sweeping segment.
        var track = new Rect(44, b.Height - 14, b.Width - 60, 4);
        dc.DrawRectangle(Theme.MakeBrush(Theme.Bone.WithAlpha(0.15)), null, track);
        var red = Theme.MakeBrush(Theme.Red);
        if (Progress > 0.01)
        {
            dc.DrawRectangle(red, null, new Rect(track.X, track.Y, track.Width * Math.Min(Progress, 1), track.Height));
        }
        else
        {
            double phase = (Angle / 10) % 1;
            double w = track.Width * 0.25;
            var seg = new Rect(track.X - w + (track.Width + w) * phase, track.Y, w, track.Height);
            seg.Intersect(track);
            if (!seg.IsEmpty) dc.DrawRectangle(red, null, seg);
        }
    }
}

public sealed class BusyHud
{
    public static readonly BusyHud Shared = new();

    private HudWindow? window;
    private readonly BusyVisual view = new();
    private DispatcherTimer? spin;
    private readonly Dictionary<Guid, (string label, double progress)> jobs = new();
    private readonly List<Guid> order = new();

    public Guid Begin(string label)
    {
        var id = Guid.NewGuid();
        jobs[id] = (label, 0);
        order.Add(id);
        // Quick jobs never flash the bar.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (jobs.ContainsKey(id)) Present();
        };
        delay.Start();
        return id;
    }

    public void Update(Guid id, double progress)
    {
        if (!jobs.ContainsKey(id)) return;
        jobs[id] = (jobs[id].label, progress);
        Refresh();
    }

    public void End(Guid id)
    {
        jobs.Remove(id);
        order.Remove(id);
        if (jobs.Count == 0)
        {
            spin?.Stop();
            spin = null;
            window?.Hide();
        }
        else
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (order.Count == 0 || !jobs.TryGetValue(order[0], out var job)) return;
        string pct = job.progress > 0.01 ? "  " + (int)(job.progress * 100) + "%" : "";
        string more = jobs.Count > 1 ? "（還有 " + (jobs.Count - 1) + " 個）" : "";
        view.Text = "下包中 · " + job.label + pct + more;
        view.Progress = job.progress;
        view.InvalidateVisual();
    }

    private void Present()
    {
        var m = Native.MonitorAt(Native.CursorPos);
        window ??= new HudWindow(view);
        int w = (int)Math.Round(BusyVisual.PanelSize.Width * m.Scale);
        int h = (int)Math.Round(BusyVisual.PanelSize.Height * m.Scale);
        int margin = (int)Math.Round(16 * m.Scale);
        Refresh();
        window.ShowAt(m.Work.Right - w - margin, m.Work.Bottom - h - (int)Math.Round(24 * m.Scale), w, h);
        if (spin == null)
        {
            SFX.Shared.Play(SfxKind.plant);
            spin = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            spin.Tick += (_, _) =>
            {
                view.Angle += 0.14;
                view.InvalidateVisual();
            };
            spin.Start();
        }
    }
}

/// <summary>After 去隱私: the red reveal markers blink in the top-right corner and fade out.</summary>
public sealed class WantedHud
{
    public static readonly WantedHud Shared = new();

    private HudWindow? window;
    private readonly WantedVisual view = new();
    private DispatcherTimer? timer;

    public void Evade(Wanted wanted, Native.MonitorInfo? monitor)
    {
        if (wanted.Level <= 0) return;
        var m = monitor ?? Native.MonitorAt(Native.CursorPos);
        window ??= new HudWindow(view);
        int w = (int)Math.Round(WantedVisual.PanelSize.Width * m.Scale);
        int h = (int)Math.Round(WantedVisual.PanelSize.Height * m.Scale);
        int x = m.Bounds.Right - w - (int)Math.Round(28 * m.Scale);
        int y = m.Bounds.Top + (int)Math.Round((28 + StatsPanel.PanelHeight(4) + 8) * m.Scale);
        view.Show(wanted);
        view.Appear = 1;
        view.Evading = true;
        view.Caption = "已解除偵查";
        window.ShowAt(x, y, w, h);

        var started = DateTime.Now;
        timer?.Stop();
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.Now - started).TotalSeconds;
            view.Phase = elapsed;
            view.InvalidateVisual();
            if (elapsed <= 1.9) return;
            timer?.Stop();
            Fade.Out(window, 0.45);
        };
        timer.Start();
    }
}
