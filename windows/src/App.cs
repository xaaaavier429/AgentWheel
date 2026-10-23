using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace AgentWheel;

/// <summary>
/// Entry point. Command-line flags are for testing (snapshots, running one action, listing slots);
/// with no flags this is the tray app.
/// </summary>
public static class Program
{
    private const string InstanceName = "AgentWheel.Instance.v1";
    private const string PreviewName = "AgentWheel.Preview.v1";

    private static string[] args = Array.Empty<string>();

    private static string? Flag(string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> PathsAfter(string name)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0) return new List<string>();
        return args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToList();
    }

    private static void ApplyOverrides()
    {
        if (Enum.TryParse<Crew>(Flag("--crew"), out var crew)) Settings.Agent = crew;
        if (Enum.TryParse<WheelSize>(Flag("--size"), out var size)) Settings.Size = size;
    }

    [STAThread]
    public static int Main(string[] argv)
    {
        args = argv;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Length > 0 && args[0].StartsWith("--")) return Command();

        // One copy at a time; launching it again just shows the demo wheel in the running copy.
        using var instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try
            {
                using var preview = EventWaitHandle.OpenExisting(PreviewName);
                preview.Set();
            }
            catch
            {
                // the other copy is still starting up
            }
            return 0;
        }
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, PreviewName);
        var app = new TrayApp(signal);
        return app.Run();
    }

    // MARK: - Command line

    private static int Command()
    {
        Native.AttachConsole(-1);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        try
        {
            if (Flag("--snapshot") is string snap)
            {
                ApplyOverrides();
                int? hover = int.TryParse(Flag("--hover"), out int h) ? h : null;
                int page = int.TryParse(Flag("--page"), out int p) ? p : 0;
                Point? center = double.TryParse(Flag("--x"), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                                double.TryParse(Flag("--y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    ? new Point(x, y)
                    : null;
                var files = PathsAfter("--files");
                var wanted = files.Count > 0 ? Privacy.Image(files[0]) : new Wanted();
                if (int.TryParse(Flag("--wanted"), out int level))
                    wanted = new Wanted { Level = level, Reasons = new List<string> { "GPS 定位", "iPhone 15 Pro", "拍攝時間" } };
                Snapshot.Wheel(files, hover, page, Flag("--bg"), Path.GetFullPath(snap), null, center, wanted);
                return 0;
            }
            if (Flag("--banner") is string banner)
            {
                Outcome? outcome = Outcomes.TryParse(Flag("--word"), out var o) ? o : null;
                Snapshot.Banner(!args.Contains("--wasted"), Flag("--title") ?? "IMG_2049.HEIC  →  JPG",
                    Flag("--detail") ?? "2.4 MB → 1.1 MB · 存到「桌面」", Flag("--bg"), Path.GetFullPath(banner), null, outcome);
                return 0;
            }
            if (args.Contains("--list"))
            {
                var context = Catalog.Context(PathsAfter("--list"));
                Console.WriteLine($"kind: {context.Kind}  headline: {context.Headline}  summary: {context.Summary}");
                foreach (var page in context.Pages)
                {
                    Console.WriteLine("[" + page.Name + "]");
                    for (int i = 0; i < page.Slots.Count; i++)
                    {
                        var slot = page.Slots[i];
                        Console.WriteLine(slot == null
                            ? $"  {i}: —"
                            : $"  {i}: {slot.Id}  {slot.Title}  {(slot.Enabled ? "" : "(disabled: " + slot.Note + ")")}");
                    }
                }
                return 0;
            }
            if (args.Contains("--ids"))
            {
                Console.WriteLine(string.Join(Environment.NewLine, Catalog.AllIds));
                return 0;
            }
            if (Flag("--sounds") is string folder)
            {
                SFX.Shared.Export(Path.GetFullPath(folder));
                Console.WriteLine("sounds → " + Path.GetFullPath(folder));
                return 0;
            }
            if (args.Contains("--preview"))
            {
                using var preview = EventWaitHandle.OpenExisting(PreviewName);
                preview.Set();
                return 0;
            }
            if (args.Contains("--scan"))
            {
                foreach (var file in PathsAfter("--scan"))
                {
                    var found = Privacy.Scan(file).GetAwaiter().GetResult();
                    Console.WriteLine($"{Path.GetFileName(file)}: {new string('★', found.Level)}{new string('☆', Math.Max(0, 5 - found.Level))}  {string.Join(" · ", found.Reasons)}");
                }
                return 0;
            }
            if (Flag("--run") is string id)
            {
                var files = PathsAfter("--run").Skip(1).ToList();
                var watch = Stopwatch.StartNew();
                try
                {
                    var summary = Engine.Run(id, id, files, _ => { }).GetAwaiter().GetResult();
                    Console.WriteLine($"OK {id} [{watch.Elapsed.TotalSeconds:0.00}s] {summary.Detail}");
                    foreach (var output in summary.Outputs) Console.WriteLine("   -> " + output);
                    return 0;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"FAIL {id}: {e.Message}");
                    return 1;
                }
            }
            Console.WriteLine("AgentWheel " + Assembly.GetExecutingAssembly().GetName().Version);
            Console.WriteLine("  --snapshot out.png [--files a b] [--hover n] [--page n] [--bg desktop.jpg] [--crew jett] [--size large] [--wanted 3]");
            Console.WriteLine("  --banner out.png [--word ace] [--title t] [--detail d] [--wasted]");
            Console.WriteLine("  --list files…   --ids   --run id files…   --scan files…   --sounds folder   --preview");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("failed: " + e);
            return 1;
        }
    }
}

/// <summary>The tray icon, its menu, and the global drag watcher.</summary>
public sealed class TrayApp
{
    private readonly EventWaitHandle previewSignal;
    private readonly Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    private readonly DragWatcher watcher = new();
    private WF.NotifyIcon? tray;

    public TrayApp(EventWaitHandle previewSignal)
    {
        this.previewSignal = previewSignal;
    }

    public int Run()
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("unobserved: " + e.Exception);
            e.SetObserved();
        };
        app.Startup += (_, _) => Start();
        app.Exit += (_, _) =>
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
        };
        return app.Run();
    }

    private void Start()
    {
        Log.Write("start: AgentWheel " + Assembly.GetExecutingAssembly().GetName().Version);
        SFX.Shared.Prepare();
        Overlay.Shared.Prepare();

        tray = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "特戰輪盤：拖著檔案按 Shift",
            Visible = true,
            ContextMenuStrip = new WF.ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildMenu(tray.ContextMenuStrip);
            e.Cancel = false;
        };
        tray.MouseUp += (_, e) =>
        {
            // A left click opens the same menu as a right click.
            if (e.Button != WF.MouseButtons.Left) return;
            typeof(WF.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);
        };

        watcher.OnShift = (p, fromShell) => Overlay.Shared.OpenForDrag(p, fromShell);
        watcher.OnRelease = () => Overlay.Shared.MouseReleased();
        watcher.IsWheelOpen = () => Overlay.Shared.IsOpen;
        watcher.Start();

        // Another copy (or `AgentWheel --preview`) asks this one to flash the demo wheel.
        var dispatcher = app.Dispatcher;
        var listener = new Thread(() =>
        {
            while (true)
            {
                previewSignal.WaitOne();
                dispatcher.InvokeAsync(() =>
                {
                    ShowDemo();
                    After(6, () =>
                    {
                        if (Overlay.Shared.IsOpen) Overlay.Shared.Close(true);
                    });
                });
            }
        }) { IsBackground = true, Name = "preview-listener" };
        listener.Start();

        if (!Settings.Welcomed)
        {
            Settings.Welcomed = true;
            tray.ShowBalloonTip(6000, "特戰輪盤已啟動", "拖著檔案時按一下 Shift，輪盤就會出現。設定在右下角通知區域的圖示裡。", WF.ToolTipIcon.None);
            After(0.8, ShowDemo);
        }
    }

    private static void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static SD.Icon LoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgentWheel.ico");
            if (stream != null) return new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through to the stock icon
        }
        return SD.SystemIcons.Application;
    }

    // MARK: - Menu

    private void BuildMenu(WF.ContextMenuStrip menu)
    {
        // Picks up sound files dropped into the custom folder, and tools installed since last time.
        SFX.Shared.Reload();
        Shell.Forget();
        menu.Items.Clear();
        menu.Items.Add(Disabled("特戰輪盤"));
        menu.Items.Add(Disabled("拖著檔案按 Shift 撥出輪盤"));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Item("看看效果（示範）", ShowDemo));
        menu.Items.Add(Item("選擇檔案開啟輪盤…", ChooseFiles));
        menu.Items.Add(new WF.ToolStripSeparator());

        var recent = new WF.ToolStripMenuItem("最近轉換（點選在檔案總管中顯示）");
        var items = Settings.Recents;
        foreach (var path in items)
        {
            string label = Path.GetFileName(path) + "  —  " + Out.FolderName(path);
            var entry = Item(label, () => Reveal(path));
            entry.Enabled = File.Exists(path) || Directory.Exists(path);
            recent.DropDownItems.Add(entry);
        }
        if (items.Count == 0) recent.DropDownItems.Add(Disabled("還沒有轉換過檔案"));
        menu.Items.Add(recent);
        menu.Items.Add(new WF.ToolStripSeparator());

        var crews = new WF.ToolStripMenuItem("角色配色");
        foreach (Crew crew in Enum.GetValues(typeof(Crew)))
        {
            var entry = Item(CrewInfo.Title(crew), () =>
            {
                Settings.Agent = crew;
                ShowDemo();
            });
            entry.Checked = Settings.Agent == crew;
            entry.Image = Swatch(CrewInfo.Tint(crew));
            crews.DropDownItems.Add(entry);
        }
        menu.Items.Add(crews);

        var sizes = new WF.ToolStripMenuItem("輪盤大小");
        foreach (WheelSize size in Enum.GetValues(typeof(WheelSize)))
        {
            var entry = Item(SizeInfo.Title(size), () =>
            {
                Settings.Size = size;
                ShowDemo();
            });
            entry.Checked = Settings.Size == size;
            sizes.DropDownItems.Add(entry);
        }
        menu.Items.Add(sizes);

        menu.Items.Add(Toggle("音效", Settings.Sound, () => Settings.Sound = !Settings.Sound));
        menu.Items.Add(Item("自訂音效資料夾…", OpenSoundFolder));
        menu.Items.Add(Toggle("完成橫幅（VICTORY / ACE）", Settings.Banner, () => Settings.Banner = !Settings.Banner));
        menu.Items.Add(Toggle("登入時自動啟動", Settings.LaunchAtLogin, ToggleLogin));
        menu.Items.Add(new WF.ToolStripSeparator());

        if (Shell.Has("ffmpeg"))
            menu.Items.Add(Disabled("彈藥充足：ffmpeg ✓" + (Office.Available ? "  Office ✓" : "")));
        else
        {
            menu.Items.Add(Disabled("未裝備：ffmpeg（影片和音訊需要它）"));
            menu.Items.Add(Item("安裝 ffmpeg…", InstallFfmpeg));
        }
        menu.Items.Add(Item("開啟紀錄資料夾", () => Open(Log.Folder)));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Item("結束特戰輪盤", Quit));
    }

    private static WF.ToolStripMenuItem Item(string title, Action action)
    {
        var entry = new WF.ToolStripMenuItem(title);
        entry.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Write("menu: " + title + " failed: " + e);
            }
        };
        return entry;
    }

    private static WF.ToolStripMenuItem Toggle(string title, bool on, Action action)
    {
        var entry = Item(title, action);
        entry.Checked = on;
        return entry;
    }

    private static WF.ToolStripMenuItem Disabled(string title) => new(title) { Enabled = false };

    private static SD.Image Swatch(System.Windows.Media.Color color)
    {
        var bitmap = new SD.Bitmap(14, 14);
        using var g = SD.Graphics.FromImage(bitmap);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(color.R, color.G, color.B));
        g.FillEllipse(brush, 1, 1, 12, 12);
        return bitmap;
    }

    // MARK: - Actions

    private static Native.POINT MonitorCenter()
    {
        var m = Native.MonitorAt(Native.CursorPos);
        return new Native.POINT(m.Bounds.Left + m.Bounds.Width / 2, m.Bounds.Top + m.Bounds.Height / 2);
    }

    private void ShowDemo()
    {
        string sample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "IMG_2049.HEIC");
        var context = Catalog.Context(new[] { sample });
        context.Headline = "IMG_2049.HEIC";
        context.Summary = "2.4 MB · 4032×3024 · HEIC";
        Overlay.Shared.Open(new[] { sample }, OverlayMode.Demo, MonitorCenter(), context);
    }

    private void ChooseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "選好檔案後，用滑鼠點輪盤上的格子",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;
        var files = dialog.FileNames.ToList();
        // Let the dialog finish closing before the full-screen overlay takes over.
        After(0.15, () => Overlay.Shared.Open(files, OverlayMode.Click, MonitorCenter()));
    }

    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Write("reveal failed: " + e.Message);
        }
    }

    private static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    /// <summary>Opens (creating if needed) the folder where sound files replace the synthesized ones.</summary>
    private static void OpenSoundFolder()
    {
        string folder = SFX.CustomFolder;
        Directory.CreateDirectory(folder);
        string readme = Path.Combine(folder, "說明.txt");
        if (!File.Exists(readme))
        {
            const string text =
                "把音效檔放進這個資料夾，就會取代內建的合成音效（支援 wav、mp3、m4a、wma、aiff）。\r\n" +
                "檔名要對應下面的名稱，例如 passed.wav。放好後重新點一次通知區域的圖示就會生效。\r\n\r\n" +
                "open        打開輪盤\r\n" +
                "tick        游標移到格子上\r\n" +
                "page        切換 武器 / 技能 頁\r\n" +
                "denied      點到不能用的格子\r\n" +
                "passed      成功（VICTORY / THRIFTY）\r\n" +
                "ace         成功（ACE，一次 5 個以上檔案）\r\n" +
                "evaded      去隱私成功（FLAWLESS）\r\n" +
                "wasted      失敗（DEFEAT）\r\n" +
                "wanted      偵測到隱私資訊\r\n" +
                "plant       開始處理（下包進度條出現）\r\n" +
                "rackPistol / rackRifle / rackBolt / rackPump / rackHeavy / pinPull / bladeDraw / meleeSwing\r\n" +
                "            各類武器的裝備聲\r\n\r\n" +
                "刪掉檔案就會換回內建音效。\r\n";
            File.WriteAllText(readme, text, new UTF8Encoding(true));
        }
        Open(folder);
    }

    private static void ToggleLogin()
    {
        try
        {
            Settings.LaunchAtLogin = !Settings.LaunchAtLogin;
        }
        catch (Exception e)
        {
            MessageBox.Show("沒能設定自動啟動：" + e.Message + "\n\n可以把 AgentWheel.exe 的捷徑放進「shell:startup」資料夾。",
                "特戰輪盤", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens a console running winget; the menu re-checks for ffmpeg the next time it opens.</summary>
    private static void InstallFfmpeg()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/k echo 正在安裝 ffmpeg，完成後關掉這個視窗即可 & winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            MessageBox.Show("沒能啟動 winget：" + e.Message + "\n\n也可以到 https://www.gyan.dev/ffmpeg/builds/ 下載，把 ffmpeg.exe 和 ffprobe.exe 放在 AgentWheel.exe 旁邊。",
                "特戰輪盤", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Quit()
    {
        watcher.Stop();
        if (Overlay.Shared.IsOpen) Overlay.Shared.Close(false);
        app.Shutdown();
    }
}
