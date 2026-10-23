using System;
using System.Windows.Threading;

namespace AgentWheel;

/// <summary>
/// Notices "something is being dragged and Shift was just pressed" anywhere on the system.
///
/// Needs no special permissions: the primary mouse button, Shift and the cursor are polled with
/// GetAsyncKeyState / GetCursorPos. Which files are being dragged is only known once Windows routes
/// the drag to the overlay, so the overlay opens in a pending state and fills in on the first drag event.
/// </summary>
public sealed class DragWatcher
{
    /// <summary>Shift went down mid-drag; the flag says whether the drag started in Explorer / on the desktop.</summary>
    public Action<Native.POINT, bool>? OnShift;
    public Action? OnRelease;
    public Func<bool> IsWheelOpen = () => false;

    private DispatcherTimer? timer;
    private bool wasDown;
    private bool moved;
    private bool lastShift;
    private Native.POINT downAt;
    private bool fromShell;

    public void Start()
    {
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    public void Stop() => timer?.Stop();

    private void Poll()
    {
        bool down = Native.LeftDown;
        bool shift = Native.ShiftDown;
        var p = Native.CursorPos;
        if (down && !wasDown)
        {
            downAt = p;
            moved = false;
            fromShell = Native.IsShellWindowAt(p);
            lastShift = shift;
        }
        // Windows starts a drag after a few pixels of movement (SM_CXDRAG); match that.
        if (down && !moved && (Math.Abs(p.X - downAt.X) > 4 || Math.Abs(p.Y - downAt.Y) > 4)) moved = true;
        if (down && moved && shift && !lastShift && !IsWheelOpen())
        {
            Log.Write("drag: shift pressed → open wheel");
            OnShift?.Invoke(p, fromShell);
        }
        if (!down && wasDown) OnRelease?.Invoke();
        lastShift = shift;
        wasDown = down;
    }
}
