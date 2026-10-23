using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentWheel;

/// <summary>Full-screen, topmost, transparent window that hosts the wheel and is the drop target for the whole screen.</summary>
public sealed class OverlayWindow : Window
{
    public readonly OverlayContent View = new();

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        Content = View;
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false, false);
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();
}

public enum OverlayMode
{
    /// <summary>Opened by Shift during an Explorer drag; dropping picks the slot.</summary>
    Drag,
    /// <summary>Opened from the menu for chosen files; clicking picks the slot.</summary>
    Click,
    /// <summary>Opened from the menu with a sample file; nothing is converted.</summary>
    Demo,
}

public sealed class Overlay
{
    public static readonly Overlay Shared = new();

    private OverlayWindow? window;
    public bool IsOpen { get; private set; }
    private OverlayMode mode = OverlayMode.Drag;
    private WheelContext? context;
    private int page;
    private int? hover;
    private Point center;
    private Point openPoint;
    private bool armed;
    private double radius = 245;
    private Native.MonitorInfo monitor = new();
    private bool rendering;
    private DateTime lastTick = DateTime.Now;
    private bool lastShift;
    private DispatcherTimer? closeTimer;
    private bool dragSeen;
    private int dragGeneration;
    private Wanted wanted = new();
    private Guid scanToken;

    private OverlayContent View => window!.View;

    /// <summary>Builds the window up front so the first Shift opens instantly.</summary>
    public void Prepare()
    {
        Ensure();
    }

    private OverlayWindow Ensure()
    {
        if (window != null) return window;
        var w = new OverlayWindow();
        w.DragEnter += (_, e) => DragMoved(e);
        w.DragOver += (_, e) => DragMoved(e);
        w.DragLeave += (_, e) => DragExited(e);
        w.Drop += (_, e) => PerformDrop(e);
        w.MouseLeftButtonDown += (_, e) => Clicked(e.GetPosition(w.View));
        w.MouseRightButtonDown += (_, _) => Close(true);
        w.MouseWheel += (_, e) =>
        {
            if (Math.Abs(e.Delta) > 3) FlipPage();
        };
        w.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close(true);
                    break;
                case Key.Tab:
                case Key.Q:
                case Key.E:
                    FlipPage();
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Space:
                    Clicked(Mouse.GetPosition(w.View));
                    break;
            }
        };
        window = w;
        return w;
    }

    private Point ToView(Native.POINT px) =>
        new Point((px.X - monitor.Bounds.Left) / monitor.Scale, (px.Y - monitor.Bounds.Top) / monitor.Scale);

    // MARK: - Open / close

    /// <summary>
    /// Opens the wheel. With no files (a drag whose contents Windows hasn't told us yet) it shows a pending
    /// wheel that fills in on the first drag event.
    /// </summary>
    public void Open(IList<string> files, OverlayMode mode, Native.POINT cursor, WheelContext? overrideContext = null)
    {
        var w = Ensure();
        closeTimer?.Stop();
        this.mode = mode;
        context = overrideContext ?? (files.Count > 0 ? Catalog.Context(files) : null);
        page = 0;
        hover = null;
        lastShift = Native.ShiftDown;
        dragSeen = false;

        monitor = Native.MonitorAt(cursor);
        var size = new Size(monitor.Bounds.Width / monitor.Scale, monitor.Bounds.Height / monitor.Scale);
        radius = Math.Min(SizeInfo.Radius(Settings.Size), (size.Height - 72) / 2);
        double margin = radius + 30;
        var mouse = ToView(cursor);
        center = new Point(Math.Clamp(mouse.X, margin, Math.Max(margin, size.Width - margin)),
                           Math.Clamp(mouse.Y, margin, Math.Max(margin, size.Height - margin)));
        openPoint = mouse;
        // When the wheel had to shift away from the cursor, wait for a deliberate move before selecting.
        armed = (center - mouse).Length < 4;

        View.Arrange(size, center, radius, context?.StatLabels.Length ?? 4);
        View.SetAccent(Theme.Accent);
        Array.Clear(View.Wheel.Glow);
        View.Wheel.ContentFade = 1;
        View.Wheel.Pop = 0;
        View.Stats.Shown = context?.Base.ToArray() ?? Array.Empty<double>();
        View.Apply(context, page, null);
        wanted = new Wanted();
        View.Wanted.Reset();
        if (mode == OverlayMode.Demo)
            Reveal(new Wanted { Level = 3, Reasons = new List<string> { "GPS 定位", "iPhone 15 Pro", "拍攝時間" } });
        else if (context != null)
            Scan(context.Files);

        w.ShowActivated = mode != OverlayMode.Drag;
        w.Opacity = 0;
        if (!w.IsVisible) w.Show();
        Native.Place(w.Handle, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
        if (mode != OverlayMode.Drag)
        {
            w.Activate();
            Native.SetForegroundWindow(w.Handle);
            w.Focus();
            Keyboard.Focus(w);
        }
        IsOpen = true;
        Log.Write($"wheel: open {mode} kind={context?.Kind.ToString() ?? "pending"} files={files.Count}");
        SFX.Shared.Play(SfxKind.open);
        StartRendering();
    }

    /// <summary>
    /// Shift pressed during a drag. The files arrive with the first drag event over the wheel. A drag that didn't
    /// start in Explorer may not carry files at all (selecting text, moving a window), so if nothing turns up
    /// quickly the wheel quietly goes away again.
    /// </summary>
    public void OpenForDrag(Native.POINT cursor, bool fromShell)
    {
        Open(new List<string>(), OverlayMode.Drag, cursor);
        if (fromShell) return;
        int generation = ++dragGeneration;
        var check = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.9) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (generation == dragGeneration && IsOpen && mode == OverlayMode.Drag && context == null && !dragSeen)
            {
                Log.Write("wheel: no files arrived, closing");
                Close(false);
            }
        };
        check.Start();
    }

    private void SetContext(IList<string> files)
    {
        if (files.Count == 0) return;
        context = Catalog.Context(files);
        var size = new Size(View.Width, View.Height);
        View.Arrange(size, center, radius, context.StatLabels.Length);
        View.Stats.Shown = context.Base.ToArray();
        View.Wheel.ContentFade = 0.1;
        View.Apply(context, page, hover);
        Scan(context.Files);
        Log.Write($"wheel: drag carries {files.Count} file(s) kind={context.Kind}");
    }

    private void Scan(List<string> files)
    {
        var token = Guid.NewGuid();
        scanToken = token;
        Task.Run(async () =>
        {
            var found = await Privacy.Scan(files);
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (IsOpen && scanToken == token) Reveal(found);
            });
        });
    }

    private void Reveal(Wanted found)
    {
        wanted = found;
        View.Wanted.Show(found);
        if (found.Level > 0)
        {
            SFX.Shared.Play(SfxKind.wanted);
            Log.Write($"wheel: revealed level {found.Level} ({string.Join(", ", found.Reasons)})");
        }
    }

    public void Close(bool animated)
    {
        if (!IsOpen || window == null) return;
        IsOpen = false;
        Log.Write("wheel: close");
        closeTimer?.Stop();
        StopRendering();
        if (animated) Fade.Out(window, 0.12);
        else window.Hide();
    }

    private void ScheduleClose(double seconds)
    {
        closeTimer?.Stop();
        closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer?.Stop();
            Close(true);
        };
        closeTimer.Start();
    }

    // MARK: - Selection

    private WheelOption? CurrentOption
    {
        get
        {
            if (context == null || hover is not int h || page >= context.Pages.Count) return null;
            var slots = context.Pages[page].Slots;
            return h < slots.Count ? slots[h] : null;
        }
    }

    private int? SlotAt(Point p)
    {
        if (!armed)
        {
            if ((p - openPoint).Length <= 12) return null;
            armed = true;
        }
        double dx = p.X - center.X, dy = center.Y - p.Y;
        double deadZone = Math.Max(28, radius * 0.6 * 0.3);
        if (Math.Sqrt(dx * dx + dy * dy) < deadZone) return null;
        double theta = Math.Atan2(dx, dy);
        if (theta < 0) theta += 2 * Math.PI;
        int index = (int)((theta + Math.PI / 8) / (Math.PI / 4)) % 8;
        // A little hysteresis so the highlight doesn't flicker on a boundary.
        if (hover is int current && current != index)
        {
            double diff = Math.Abs(theta - current * Math.PI / 4);
            if (diff > Math.PI) diff = 2 * Math.PI - diff;
            if (diff < Math.PI / 8 + 0.07) index = current;
        }
        return index;
    }

    private void UpdateHover(Point p)
    {
        if (context == null) return;
        int? next = SlotAt(p);
        if (next == hover) return;
        hover = next;
        View.Apply(context, page, hover);
        if (CurrentOption != null) SFX.Shared.Play(SfxKind.tick);
    }

    public void FlipPage()
    {
        if (!IsOpen || context == null || context.Pages.Count < 2) return;
        page = (page + 1) % context.Pages.Count;
        View.Wheel.ContentFade = 0.1;
        View.Apply(context, page, hover);
        SFX.Shared.Play(SfxKind.page);
    }

    // MARK: - Animation tick

    private void StartRendering()
    {
        lastTick = DateTime.Now;
        if (rendering) return;
        CompositionTarget.Rendering += OnFrame;
        rendering = true;
    }

    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= OnFrame;
        rendering = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsOpen || window == null) return;
        var now = DateTime.Now;
        double dt = Math.Min(0.05, (now - lastTick).TotalSeconds);
        lastTick = now;

        if (window.Opacity < 1) window.Opacity = Math.Min(1, window.Opacity + dt / 0.12);
        UpdateHover(ToView(Native.CursorPos));
        bool shift = Native.ShiftDown;
        if (shift && !lastShift) FlipPage();
        lastShift = shift;

        var wheel = View.Wheel;
        bool dirty = false;
        var slots = context != null && page < context.Pages.Count ? context.Pages[page].Slots : new List<WheelOption?>();
        for (int i = 0; i < 8; i++)
        {
            double target = 0;
            if (i == hover && i < slots.Count && slots[i] is WheelOption option) target = option.Enabled ? 1 : 0.35;
            double g = wheel.Glow[i];
            if (g == target) continue;
            wheel.Glow[i] = Math.Abs(target - g) < 0.004 ? target : g + (target - g) * Math.Min(1, dt * 24);
            dirty = true;
        }
        if (wheel.ContentFade < 1)
        {
            wheel.ContentFade = Math.Min(1, wheel.ContentFade + dt * 7);
            dirty = true;
        }
        if (wheel.Pop < 1)
        {
            wheel.Pop = Math.Min(1, wheel.Pop + dt / 0.16);
            dirty = true;
        }
        if (dirty) wheel.InvalidateVisual();

        var stats = View.Stats;
        bool statsDirty = false;
        if (stats.Shown.Length != stats.Target.Length) stats.Shown = stats.Target.ToArray();
        for (int i = 0; i < stats.Target.Length; i++)
        {
            double current = stats.Shown[i], target = stats.Target[i];
            if (current == target) continue;
            stats.Shown[i] = Math.Abs(target - current) < 0.002 ? target : current + (target - current) * Math.Min(1, dt * 14);
            statsDirty = true;
        }
        if (statsDirty) stats.InvalidateVisual();

        var stars = View.Wanted;
        if (stars.Level > 0)
        {
            stars.Phase += dt;
            if (stars.Appear < 1) stars.Appear = Math.Min(1, stars.Appear + dt / 0.9);
            // Pointing at 去隱私 previews clearing the reveal: the markers start to blink.
            var current = CurrentOption;
            bool lyingLow = current != null && current.Enabled && current.Id.EndsWith(".strip");
            stars.Evading = lyingLow;
            stars.Caption = lyingLow ? "放開 = 解除偵查" : null;
            stars.InvalidateVisual();
        }
    }

    // MARK: - Drag destination

    private static List<string> FilesIn(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
                return paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }
        catch
        {
            // some sources fail to render the list
        }
        return new List<string>();
    }

    private void DragMoved(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsOpen || window == null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        closeTimer?.Stop();
        if (!dragSeen)
        {
            dragSeen = true;
            Log.Write("wheel: drag entered overlay");
        }
        if (context == null) SetContext(FilesIn(e.Data));
        UpdateHover(e.GetPosition(window.View));
        e.Effects = CurrentOption?.Enabled == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void DragExited(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsOpen || mode != OverlayMode.Drag) return;
        Log.Write("wheel: drag left overlay");
        ScheduleClose(0.2);
    }

    private void PerformDrop(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsOpen || window == null) return;
        var files = FilesIn(e.Data);
        if (context == null) SetContext(files);
        UpdateHover(e.GetPosition(window.View));
        var option = CurrentOption;
        Log.Write($"wheel: drop on slot {hover?.ToString() ?? "none"} → {option?.Id ?? "nothing"}");
        if (option == null || !option.Enabled || context == null)
        {
            e.Effects = DragDropEffects.None;
            Close(true);
            return;
        }
        e.Effects = DragDropEffects.Copy;
        var targets = files.Count > 0 ? files : context.Files;
        var m = monitor;
        var found = wanted;
        Close(true);
        Runner.Shared.Run(option, targets, m, found);
    }

    /// <summary>The physical button went up; the drop callbacks normally handle everything.</summary>
    public void MouseReleased()
    {
        if (!IsOpen || mode != OverlayMode.Drag) return;
        ScheduleClose(0.3);
    }

    // MARK: - Click mode

    private void Clicked(Point p)
    {
        if (!IsOpen || mode == OverlayMode.Drag || context == null) return;
        UpdateHover(p);
        var option = CurrentOption;
        if (option == null)
        {
            Close(true);
            return;
        }
        if (!option.Enabled)
        {
            SFX.Shared.Play(SfxKind.denied);
            return;
        }
        var m = monitor;
        Close(true);
        if (mode == OverlayMode.Demo) Runner.Shared.Demo(option, context, m);
        else Runner.Shared.Run(option, context.Files, m, wanted);
    }
}
