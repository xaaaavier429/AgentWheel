using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWheel;

/// <summary>An error whose message is shown to the user as is.</summary>
public sealed class FWError : Exception
{
    public FWError(string message) : base(message) { }
}

public delegate void ProgressFn(double progress);

/// <summary>What one action produced for one input file.</summary>
public sealed class OpResult
{
    public List<string> Outputs { get; set; } = new();
    public string? Note { get; set; }
    /// <summary>Text to put on the clipboard once the whole batch is done.</summary>
    public string? Clip { get; set; }

    public OpResult() { }

    public OpResult(params string[] outputs)
    {
        Outputs = outputs.ToList();
    }
}

public static class Fmt
{
    /// <summary>Sizes the way File Explorer shows them: 1024-based, "950 KB", "2.4 MB".</summary>
    public static string Bytes(long n)
    {
        if (n < 1024) return n + " 位元組";
        double v = n / 1024.0;
        if (v < 1000) return Math.Ceiling(v).ToString("0", CultureInfo.InvariantCulture) + " KB";
        v /= 1024;
        if (v < 1000) return v.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        v /= 1024;
        return v.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    public static long Size(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}

/// <summary>Where results go: next to the source file, never overwriting anything.</summary>
public static class Out
{
    public static string Downloads =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string Dir(string source)
    {
        string dir = Path.GetDirectoryName(source) ?? "";
        if (dir.Length > 0 && IsWritable(dir)) return dir;
        Directory.CreateDirectory(Downloads);
        return Downloads;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".agentwheel-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"photo.heic" → "photo.jpg" (or "photo 壓縮.jpg" with a suffix), made unique.</summary>
    public static string Target(string source, string? suffix, string ext)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(source) + (suffix != null ? " " + suffix : "");
        return Unique(Dir(source), name, ext);
    }

    public static string Unique(string dir, string name, string ext)
    {
        string dot = ext.Length == 0 ? "" : "." + ext;
        string candidate = System.IO.Path.Combine(dir, name + dot);
        int n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(dir, name + " " + n + dot);
            n++;
        }
        return candidate;
    }

    public static string Folder(string source, string suffix)
    {
        string dir = Unique(Dir(source), System.IO.Path.GetFileNameWithoutExtension(source) + " " + suffix, "");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string TempDir()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentWheel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Display name of the folder a result went to ("下載", "桌面", or the folder's own name).</summary>
    public static string FolderName(string file)
    {
        string dir = System.IO.Path.GetDirectoryName(file) ?? "";
        string trimmed = dir.TrimEnd('\\', '/');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)) return "桌面";
        if (string.Equals(trimmed, System.IO.Path.Combine(home, "Downloads"), StringComparison.OrdinalIgnoreCase)) return "下載";
        if (string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)) return "文件";
        if (string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), StringComparison.OrdinalIgnoreCase)) return "圖片";
        string name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}

/// <summary>Runs work on a fresh STA thread (WPF rendering, the clipboard and Office automation need one).</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static Task Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}

public static class ClipboardText
{
    public static Task Set(string text) => Sta.Run(() =>
    {
        // The clipboard can be briefly locked by another app; retry a few times.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return;
            }
            catch when (attempt < 8)
            {
                Thread.Sleep(60);
            }
        }
    });
}

/// <summary>
/// External command-line tools. ffmpeg ships next to the app in the release zip; otherwise it is
/// looked up on PATH and in winget's folder. Slots that need a missing tool show as "未裝備".
/// </summary>
public static class Shell
{
    private static readonly Dictionary<string, string?> Cache = new();
    private static readonly object Gate = new();

    public static string? Find(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var hit)) return hit;
            string exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            var dirs = new List<string>
            {
                AppContext.BaseDirectory,
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "tools"),
            };
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null) dirs.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links"));
            dirs.Add(@"C:\ffmpeg\bin");
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
            string? found = null;
            foreach (var dir in dirs)
            {
                try
                {
                    string candidate = System.IO.Path.Combine(dir.Trim().Trim('"'), exe);
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
                catch
                {
                    // malformed PATH entry
                }
            }
            Cache[name] = found;
            return found;
        }
    }

    public static void Forget()
    {
        lock (Gate) Cache.Clear();
    }

    public static bool Has(string name) => Find(name) != null;

    public static string? Ffmpeg => Find("ffmpeg");

    public static string? Ffprobe => Find("ffprobe");

    public static string InstallHint(string name) => name == "ffmpeg"
        ? "需要 ffmpeg：點右下角通知區域的特戰輪盤圖示 →「安裝 ffmpeg…」"
        : "需要先安裝 " + name;

    /// <summary>Runs a tool to completion. <paramref name="onLine"/> receives stdout line by line.</summary>
    public static async Task<string> Run(string exe, IEnumerable<string> args, string? cwd = null, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd != null) psi.WorkingDirectory = cwd;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) stderr.AppendLine(e.Data);
        };
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new FWError("無法執行 " + System.IO.Path.GetFileName(exe) + "：" + e.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.WaitForExit(); // flushes the async output handlers
        if (process.ExitCode != 0)
        {
            string last;
            lock (stderr)
            {
                last = stderr.ToString().Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0)
                       ?? "結束代碼 " + process.ExitCode;
            }
            throw new FWError(System.IO.Path.GetFileNameWithoutExtension(exe) + " 出錯：" + Fmt.Trunc(last, 140));
        }
        lock (stdout) return stdout.ToString();
    }
}

/// <summary>A small rolling log in %LOCALAPPDATA%\AgentWheel, for figuring out what a drag did.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Folder
    {
        get
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentWheel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Write(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (Gate)
            {
                try
                {
                    string file = System.IO.Path.Combine(Folder, "AgentWheel.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 512_000) File.Delete(file);
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
                catch
                {
                    // logging must never break anything
                }
            }
        });
    }
}
