using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AgentWheel;

/// <summary>Highlight colour, named after the Valorant agent whose signature colour it borrows.</summary>
public enum Crew
{
    valorant,
    jett,
    phoenix,
    sage,
    reyna,
    viper,
}

public enum WheelSize
{
    small,
    medium,
    large,
}

public static class CrewInfo
{
    public static string Title(Crew crew) => crew switch
    {
        Crew.valorant => "特戰紅（預設）",
        Crew.jett => "Jett · 冰藍",
        Crew.phoenix => "Phoenix · 火橘",
        Crew.sage => "Sage · 青綠",
        Crew.reyna => "Reyna · 紫",
        Crew.viper => "Viper · 毒綠",
        _ => crew.ToString(),
    };

    public static Color Tint(Crew crew) => crew switch
    {
        Crew.jett => Theme.Rgb(0.55, 0.85, 0.98),
        Crew.phoenix => Theme.Rgb(1.00, 0.55, 0.20),
        Crew.sage => Theme.Rgb(0.33, 0.86, 0.76),
        Crew.reyna => Theme.Rgb(0.72, 0.36, 0.96),
        Crew.viper => Theme.Rgb(0.40, 0.88, 0.30),
        _ => Theme.Red,
    };
}

public static class SizeInfo
{
    public static string Title(WheelSize size) => size switch
    {
        WheelSize.small => "小",
        WheelSize.large => "大",
        _ => "中",
    };

    public static double Radius(WheelSize size) => size switch
    {
        WheelSize.small => 235,
        WheelSize.large => 330,
        _ => 285,
    };
}

public static class Theme
{
    public static Color Rgb(double r, double g, double b, double a = 1) =>
        System.Windows.Media.Color.FromArgb(Byte(a), Byte(r), Byte(g), Byte(b));

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    public static Color White(double white, double alpha = 1) => Rgb(white, white, white, alpha);

    public static Color WithAlpha(this Color c, double a) => System.Windows.Media.Color.FromArgb(Byte(a), c.R, c.G, c.B);

    /// <summary>Blends <paramref name="a"/> towards <paramref name="b"/> by <paramref name="t"/>.</summary>
    public static Color Mix(this Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * Math.Clamp(t, 0, 1));
        return System.Windows.Media.Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Pen MakePen(Color c, double width, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        var p = new Pen(MakeBrush(c), width) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        p.Freeze();
        return p;
    }

    public static Color Accent => CrewInfo.Tint(Settings.Agent);
    /// <summary>Valorant red (#FF4655).</summary>
    public static readonly Color Red = Rgb(1.00, 0.275, 0.333);
    /// <summary>Deep navy used for panels (#0F1923).</summary>
    public static readonly Color Navy = Rgb(0.059, 0.098, 0.137);
    /// <summary>Off-white "bone" used for text and lines (#ECE8E1).</summary>
    public static readonly Color Bone = Rgb(0.925, 0.910, 0.882);
    public static readonly Color Victory = Rgb(0.33, 0.93, 0.78);
    public static readonly Color Gold = Rgb(0.98, 0.80, 0.36);
    public static readonly Color Wasted = Rgb(1.00, 0.275, 0.333);
    public static readonly Color Gain = Rgb(0.33, 0.86, 0.76);
    public static readonly Color Loss = Rgb(1.00, 0.275, 0.333);
}

public enum HudWeight
{
    Medium,
    Demi,
    Bold,
    Heavy,
}

public static class Fonts
{
    // Segoe UI for Latin, Microsoft JhengHei UI picks up the Traditional Chinese.
    private static readonly FontFamily HudFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Microsoft JhengHei");
    private static readonly FontFamily BadgeFamily = new FontFamily("Impact, Microsoft JhengHei UI, Microsoft JhengHei");
    private static readonly FontFamily SymbolFamily = new FontFamily("Segoe UI Symbol, Segoe UI");

    public static Typeface Hud(HudWeight weight = HudWeight.Demi) => new Typeface(HudFamily, FontStyles.Normal, weight switch
    {
        HudWeight.Medium => FontWeights.Normal,
        HudWeight.Demi => FontWeights.SemiBold,
        HudWeight.Bold => FontWeights.Bold,
        _ => FontWeights.Black,
    }, FontStretches.Normal);

    /// <summary>Chunky condensed face for the big format labels.</summary>
    public static Typeface Badge => new Typeface(BadgeFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Face for the VICTORY / ACE / DEFEAT splash.</summary>
    public static Typeface Splash => Badge;

    public static Typeface Symbol => new Typeface(SymbolFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static Typeface Serif => new Typeface(new FontFamily("Georgia, Times New Roman"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
}

/// <summary>Settings saved as JSON in %APPDATA%\AgentWheel\settings.json.</summary>
public static class Settings
{
    private sealed class Data
    {
        public string Crew { get; set; } = "valorant";
        public string WheelSize { get; set; } = "medium";
        public bool Sound { get; set; } = true;
        public bool Banner { get; set; } = true;
        public bool Welcomed { get; set; }
        public List<string> Recents { get; set; } = new();
    }

    private static Data? data;
    private static readonly object Gate = new();

    public static string Folder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentWheel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static Data Current
    {
        get
        {
            lock (Gate)
            {
                if (data != null) return data;
                try
                {
                    if (File.Exists(FilePath)) data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                }
                catch
                {
                    data = null;
                }
                data ??= new Data();
                return data;
            }
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // settings are a convenience
            }
        }
    }

    public static Crew Agent
    {
        get => Enum.TryParse<Crew>(Current.Crew, out var c) ? c : Crew.valorant;
        set { Current.Crew = value.ToString(); Save(); }
    }

    public static WheelSize Size
    {
        get => Enum.TryParse<WheelSize>(Current.WheelSize, out var s) ? s : WheelSize.medium;
        set { Current.WheelSize = value.ToString(); Save(); }
    }

    public static bool Sound
    {
        get => Current.Sound;
        set { Current.Sound = value; Save(); }
    }

    public static bool Banner
    {
        get => Current.Banner;
        set { Current.Banner = value; Save(); }
    }

    public static bool Welcomed
    {
        get => Current.Welcomed;
        set { Current.Welcomed = value; Save(); }
    }

    public static IReadOnlyList<string> Recents => Current.Recents.ToList();

    /// <summary>The last few results, for the menu's "最近轉換" list.</summary>
    public static void AddRecents(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        var merged = list.Concat(Current.Recents.Where(p => !list.Contains(p, StringComparer.OrdinalIgnoreCase))).Take(8).ToList();
        Current.Recents = merged;
        Save();
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AgentWheel";

    /// <summary>"登入時自動啟動": an entry under HKCU\…\Run pointing at this exe.</summary>
    public static bool LaunchAtLogin
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunName) is string;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue(RunName, false);
        }
    }
}
