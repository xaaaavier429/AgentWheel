using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentWheel;

public enum SfxKind
{
    open, tick, page, passed, ace, wasted, denied, wanted, evaded, plant,
    rackPistol, rackRifle, rackBolt, rackPump, rackHeavy, pinPull, bladeDraw, meleeSwing,
}

/// <summary>
/// Synthesized sounds (no audio files shipped): buy-menu ticks, a weapon-handling sound per action,
/// kill-confirm dings for a finished job (five for ACE), radar pings for a reveal and a spike-style
/// detonation for a failure. Any of them can be replaced by a file in <see cref="CustomFolder"/>.
/// </summary>
public sealed class SFX
{
    public static readonly SFX Shared = new();

    private const double Rate = 44_100.0;
    private uint seed = 0x9E37_79B9;
    private readonly Dictionary<SfxKind, string> synthesized = new();
    private readonly Dictionary<SfxKind, string> sources = new();
    private readonly Dictionary<SfxKind, List<Voice>> pool = new();

    private sealed class Voice
    {
        public readonly MediaPlayer Player = new();
        public bool Busy;
    }

    /// <summary>Drop &lt;kind&gt;.wav (or .mp3 / .m4a / .wma) here to replace a synthesized sound.</summary>
    public static string CustomFolder => Path.Combine(Settings.Folder, "Sounds");

    private static string CacheFolder
    {
        get
        {
            string dir = Path.Combine(Log.Folder, "sfx-v1");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Renders every sound to the cache in the background, then loads the players.</summary>
    public void Prepare()
    {
        Task.Run(() =>
        {
            foreach (SfxKind kind in Enum.GetValues(typeof(SfxKind)))
            {
                string path = Path.Combine(CacheFolder, kind + ".wav");
                try
                {
                    File.WriteAllBytes(path, Wav(Render(kind)));
                    lock (synthesized) synthesized[kind] = path;
                }
                catch (Exception e)
                {
                    Log.Write("sfx: render " + kind + " failed: " + e.Message);
                }
            }
            Application.Current?.Dispatcher.InvokeAsync(Reload);
        });
    }

    /// <summary>Re-reads the custom folder; anything not found there falls back to the synthesized sound.</summary>
    public void Reload()
    {
        foreach (SfxKind kind in Enum.GetValues(typeof(SfxKind)))
        {
            string? path;
            lock (synthesized) synthesized.TryGetValue(kind, out path);
            foreach (string ext in new[] { "wav", "mp3", "m4a", "wma", "aiff" })
            {
                string custom = Path.Combine(CustomFolder, kind + "." + ext);
                if (File.Exists(custom))
                {
                    path = custom;
                    break;
                }
            }
            if (path == null) continue;
            if (sources.TryGetValue(kind, out var old) && old == path) continue;
            sources[kind] = path;
            if (pool.TryGetValue(kind, out var voices))
                foreach (var v in voices) v.Player.Close();
            pool[kind] = new List<Voice> { NewVoice(kind, path) };
        }
    }

    private Voice NewVoice(SfxKind kind, string path)
    {
        var voice = new Voice();
        voice.Player.Volume = Volume(kind);
        voice.Player.MediaEnded += (_, _) =>
        {
            voice.Busy = false;
            voice.Player.Stop();
        };
        voice.Player.MediaFailed += (_, _) => voice.Busy = false;
        voice.Player.Open(new Uri(path));
        return voice;
    }

    public void Play(SfxKind kind)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => Play(kind));
            return;
        }
        if (!Settings.Sound || !sources.TryGetValue(kind, out var path)) return;
        if (!pool.TryGetValue(kind, out var voices))
        {
            voices = new List<Voice>();
            pool[kind] = voices;
        }
        var idle = voices.FirstOrDefault(v => !v.Busy);
        if (idle == null)
        {
            if (voices.Count >= 4) return;
            idle = NewVoice(kind, path);
            voices.Add(idle);
        }
        idle.Busy = true;
        idle.Player.Position = TimeSpan.Zero;
        idle.Player.Play();
    }

    public void Play(SfxKind kind, double afterSeconds)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(afterSeconds) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Play(kind);
        };
        t.Start();
    }

    private static double Volume(SfxKind kind) => kind switch
    {
        SfxKind.tick => 0.35,
        SfxKind.page => 0.4,
        SfxKind.open => 0.5,
        SfxKind.passed => 0.5,
        SfxKind.ace => 0.5,
        SfxKind.plant => 0.3,
        SfxKind.wasted => 0.75,
        SfxKind.denied => 0.35,
        SfxKind.wanted => 0.32,
        SfxKind.evaded => 0.4,
        _ => 0.65,
    };

    /// <summary>Writes every built-in sound to a folder as .wav (used to audition them).</summary>
    public void Export(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (SfxKind kind in Enum.GetValues(typeof(SfxKind)))
            File.WriteAllBytes(Path.Combine(folder, kind + ".wav"), Wav(Render(kind)));
    }

    // MARK: - Synthesis kit

    private double Noise()
    {
        seed = unchecked(seed * 1_664_525u + 1_013_904_223u);
        return seed / (double)uint.MaxValue * 2 - 1;
    }

    /// <summary>State-variable band-pass: shapes noise into metal friction, swooshes and spray.</summary>
    private sealed class BandPass
    {
        private double low;
        private double band;

        public double Run(double x, double cutoff, double damping, double rate)
        {
            double f = 2 * Math.Sin(Math.PI * Math.Min(cutoff, rate / 6.5) / rate);
            low += f * band;
            double high = x - low - damping * band;
            band += f * high;
            return band;
        }
    }

    private static double[] Samples(double seconds, Func<double, double> f)
    {
        int n = Math.Max(1, (int)(seconds * Rate));
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = f(i / Rate);
        return a;
    }

    private static float[] Clip(double seconds, Func<double, double> f) => Samples(seconds, f).Select(v => (float)v).ToArray();

    /// <summary>Lays parts down at their start times, with a soft clip.</summary>
    private static float[] Mix(double seconds, IEnumerable<(double at, double[] part)> parts)
    {
        var output = new double[(int)(seconds * Rate)];
        foreach (var (at, part) in parts)
        {
            int offset = (int)(at * Rate);
            for (int i = 0; i < part.Length && offset + i < output.Length; i++)
                if (offset + i >= 0) output[offset + i] += part[i];
        }
        return output.Select(v => (float)(Math.Tanh(v * 1.1) / 1.1)).ToArray();
    }

    private double Click(double t, double pitch)
    {
        if (t < 0) return 0;
        double body = Math.Exp(-t * 170) * (0.6 * Math.Sin(2 * Math.PI * pitch * t) + 0.22 * Math.Sin(2 * Math.PI * pitch * 2.01 * t));
        return body + Noise() * Math.Exp(-t * 1100) * 0.3;
    }

    /// <summary>A metal-on-metal clack: band-passed transient plus inharmonic ringing.</summary>
    private double[] Clack(double tone, double gain)
    {
        var bp = new BandPass();
        return Samples(0.16, u =>
        {
            double w = 2 * Math.PI * tone * u;
            double hit = bp.Run(Noise(), 3300, 0.6, Rate) * Math.Exp(-u * 380) * 1.3;
            double p1 = Math.Sin(w);
            double p2 = 0.5 * Math.Exp(-u * 30) * Math.Sin(w * 2.76);
            double p3 = 0.25 * Math.Exp(-u * 60) * Math.Sin(w * 5.4);
            double ring = (p1 + p2 + p3) * Math.Exp(-u * 55) * 0.32;
            double tick = Noise() * Math.Exp(-u * 2500) * 0.45;
            return (hit + ring + tick) * gain;
        });
    }

    /// <summary>Low body knock with a quick pitch drop.</summary>
    private double[] Thump(double freq, double decay, double gain)
    {
        double phase = 0;
        return Samples(Math.Min(2.6, 6 / decay), u =>
        {
            phase += 2 * Math.PI * freq * (1 + 0.8 * Math.Exp(-u * 50)) / Rate;
            return Math.Sin(phase) * Math.Exp(-u * decay) * gain;
        });
    }

    /// <summary>Sliding friction (a slide or bolt travelling), noise swept between two pitches.</summary>
    private double[] Scrape(double duration, double f0, double f1, double gain, double damping = 0.9)
    {
        var bp = new BandPass();
        return Samples(duration, u =>
        {
            double k = u / duration;
            double env = Math.Pow(Math.Sin(Math.PI * k), 0.7);
            return bp.Run(Noise(), f0 + (f1 - f0) * k, damping, Rate) * env * gain;
        });
    }

    /// <summary>A bell-like metal ring.</summary>
    private static double[] Ring(double freq, double decay, double gain)
    {
        var partials = new[] { (1.0, 1.0, 1.0), (2.41, 0.5, 1.4), (3.93, 0.3, 2.0), (5.6, 0.18, 3.0) };
        return Samples(Math.Min(2, 7 / decay), u =>
        {
            double sum = 0;
            foreach (var (mult, amp, dk) in partials) sum += Math.Sin(2 * Math.PI * freq * mult * u) * amp * Math.Exp(-u * decay * dk);
            return sum * Math.Min(1, u / 0.002) * gain;
        });
    }

    private double[] Whoosh(double duration, double f0, double f1, double gain)
    {
        var bp = new BandPass();
        return Samples(duration, u =>
        {
            double k = u / duration;
            return bp.Run(Noise(), f0 + (f1 - f0) * k, 1.2, Rate) * Math.Pow(Math.Sin(Math.PI * k), 1.5) * gain;
        });
    }

    /// <summary>Soft saw-like synth voice: higher harmonics die away faster than the fundamental.</summary>
    private static double SynthVoice(double freq, double u, double bright)
    {
        double sum = 0;
        for (int k = 1; k <= 6; k++) sum += Math.Sin(2 * Math.PI * freq * k * u) / k * Math.Exp(-u * bright * k);
        return sum;
    }

    /// <summary>A short clean digital blip, like a menu UI sound.</summary>
    private static double Blip(double t, double freq, double length)
    {
        if (t < 0 || t >= length) return 0;
        double k = 1 - t / length;
        double env = Math.Min(1, t / 0.002) * k * k;
        return (Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2 * t)) * env;
    }

    /// <summary>A glassy, crisp "ding" with a tiny click on the front: the kill-confirm style hit.</summary>
    private double[] Ding(double freq, double gain) => Samples(0.6, u =>
    {
        double attack = Math.Min(1, u / 0.0015);
        double p1 = Math.Sin(2 * Math.PI * freq * u) * Math.Exp(-u * 7);
        double p2 = 0.45 * Math.Sin(2 * Math.PI * freq * 2.0 * u) * Math.Exp(-u * 12);
        double p3 = 0.25 * Math.Sin(2 * Math.PI * freq * 3.01 * u) * Math.Exp(-u * 20);
        double p4 = 0.12 * Math.Sin(2 * Math.PI * freq * 4.2 * u) * Math.Exp(-u * 30);
        double click = Noise() * Math.Exp(-u * 1800) * 0.4;
        return ((p1 + p2 + p3 + p4) * attack + click) * gain;
    });

    /// <summary>A muted, filtered UI click.</summary>
    private double[] Tk(double gain)
    {
        var bp = new BandPass();
        return Samples(0.03, u =>
        {
            double air = bp.Run(Noise(), 4200, 0.5, Rate) * Math.Exp(-u * 260) * 0.5;
            double body = Math.Sin(2 * Math.PI * 1250 * u) * Math.Exp(-u * 180) * 0.14;
            return (air + body) * gain;
        });
    }

    /// <summary>One synth note as a sample buffer.</summary>
    private static double[] Note(double freq, double length, double attack, double decay, double bright, double gain) =>
        Samples(length, u => SynthVoice(freq, u, bright) * Math.Min(1, u / attack) * Math.Exp(-u * decay) * gain);

    private static (double, double[]) At(double at, double[] part) => (at, part);

    // MARK: - Sounds

    public float[] Render(SfxKind kind)
    {
        switch (kind)
        {
            case SfxKind.tick:
                // Buy-menu hover: a soft, muted tick.
                return Mix(0.035, new[] { At(0, Tk(0.8)) });

            case SfxKind.page:
                return Mix(0.12, new[] { At(0, Tk(1.0)), At(0.05, Tk(0.8)) });

            case SfxKind.denied:
                // Not enough credits: two short muted bonks.
                return Mix(0.25, new[] { At(0, Thump(150, 40, 0.5)), At(0.1, Thump(130, 40, 0.45)) });

            case SfxKind.open:
            {
                // Buy menu opening: a sub "fwump", a falling breath of air and a small glassy glint.
                double phase = 0;
                var fwump = Samples(0.35, u =>
                {
                    phase += 2 * Math.PI * (140 - 90 * Math.Min(u / 0.25, 1)) / Rate;
                    return Math.Sin(phase) * Math.Min(1, u / 0.01) * Math.Exp(-u * 9) * 0.6;
                });
                var bp = new BandPass();
                var air = Samples(0.25, u =>
                {
                    double k = u / 0.25;
                    return bp.Run(Noise(), 5000 - 3000 * k, 0.8, Rate) * Math.Pow(1 - k, 2) * 0.25;
                });
                var glint = Samples(0.4, u =>
                    (Math.Sin(2 * Math.PI * 2637 * u) + 0.6 * Math.Sin(2 * Math.PI * 3951 * u)) * Math.Min(1, u / 0.002) * Math.Exp(-u * 14) * 0.08);
                return Mix(0.45, new[] { At(0, fwump), At(0, air), At(0.03, glint) });
            }

            case SfxKind.passed:
                // One kill-confirm ding over a punchy sub.
                return Mix(0.8, new[] { At(0, Ding(1760, 0.32)), At(0.004, Ding(2637, 0.1)), At(0, Thump(70, 14, 0.55)) });

            case SfxKind.ace:
            {
                // ACE: five dings stepping up in pitch, one per "kill", then a bright glint.
                var parts = new List<(double, double[])>();
                double[] freqs = { 1568, 1760, 1976, 2349, 2637 };
                for (int i = 0; i < freqs.Length; i++)
                {
                    parts.Add(At(i * 0.1, Ding(freqs[i], 0.26)));
                    parts.Add(At(i * 0.1, Thump(70, 16, 0.3)));
                }
                parts.Add(At(0.45, Ding(3520, 0.1)));
                return Mix(1.3, parts);
            }

            case SfxKind.wanted:
            {
                // Revealed: a quick upward radar chirp and a high ping with a faint echo.
                double phase = 0;
                var chirp = Samples(0.09, u =>
                {
                    phase += 2 * Math.PI * (700 + 1900 * u / 0.09) / Rate;
                    return Math.Sin(phase) * Math.Sin(Math.PI * u / 0.09) * 0.22;
                });
                var ping = Samples(0.7, u => Math.Sin(2 * Math.PI * 2400 * u) * Math.Min(1, u / 0.002) * Math.Exp(-u * 8) * 0.22);
                return Mix(1.0, new[] { At(0, chirp), At(0.09, ping), At(0.3, ping.Select(v => v * 0.3).ToArray()) });
            }

            case SfxKind.evaded:
            {
                // Reveal cleared, like a defuse: rapid dits, then a glassy two-note resolve.
                var dits = Samples(0.3, u =>
                {
                    double sum = 0;
                    for (int i = 0; i < 6; i++) sum += Blip(u - i * 0.045, 2000, 0.03);
                    return sum * 0.18;
                });
                return Mix(1.1, new[] { At(0, dits), At(0.3, Ding(1319, 0.2)), At(0.3, Ding(1976, 0.14)), At(0.3, Thump(60, 12, 0.4)) });
            }

            case SfxKind.plant:
            {
                // Work started: a rising charge-up whine and one confirming beep.
                double phase = 0;
                var whine = Samples(0.5, u =>
                {
                    phase += 2 * Math.PI * (500 + 900 * u / 0.5) / Rate;
                    return (Math.Sin(phase) + 0.3 * Math.Sin(phase * 2)) * Math.Min(1, u / 0.05) * 0.12;
                });
                var beep = Samples(0.2, u => Blip(u, 1900, 0.12) * 0.3);
                return Mix(0.75, new[] { At(0, whine), At(0.5, beep) });
            }

            case SfxKind.wasted:
            {
                // Round lost, spike-style: beeps that speed up, then a heavy detonation.
                var beepTimes = new List<double>();
                double at = 0, gap = 0.18;
                while (at < 0.75)
                {
                    beepTimes.Add(at);
                    at += gap;
                    gap = Math.Max(0.04, gap * 0.78);
                }
                var beeps = Samples(0.8, u =>
                {
                    double sum = 0;
                    foreach (double start in beepTimes) sum += Blip(u - start, 2100, 0.035);
                    return sum * 0.2;
                });
                var impact = Thump(42, 2.2, 1.2);
                double rumble = 0;
                for (int i = 0; i < impact.Length; i++)
                {
                    double u = i / Rate;
                    rumble += 0.04 * (Noise() - rumble);
                    impact[i] += rumble * Math.Exp(-u * 3.5) * 2.4;
                }
                var bp = new BandPass();
                var blast = Samples(1.2, u => bp.Run(Noise(), 3000 - 2200 * Math.Min(u, 1), 1.0, Rate) * Math.Exp(-u * 3) * 0.5);
                const double boom = 0.82;
                return Mix(3.2, new[]
                {
                    At(0, beeps), At(boom, impact), At(boom, blast),
                    At(boom + 0.12, impact.Select(v => v * 0.3).ToArray()), At(boom + 0.26, impact.Select(v => v * 0.15).ToArray()),
                });
            }

            case SfxKind.rackPistol:
                return Mix(0.45, new[] { At(0, Clack(2400, 0.7)), At(0.015, Scrape(0.07, 1800, 2600, 0.45)), At(0.12, Clack(1900, 1.0)), At(0.12, Thump(130, 30, 0.6)) });

            case SfxKind.rackRifle:
                return Mix(0.55, new[] { At(0, Scrape(0.09, 1200, 2000, 0.5)), At(0, Clack(2100, 0.6)), At(0.2, Clack(1500, 1.1)), At(0.2, Thump(95, 22, 0.8)), At(0.2, Ring(1800, 14, 0.22)) });

            case SfxKind.rackBolt:
                return Mix(0.75, new[]
                {
                    At(0, Clack(3000, 0.5)), At(0.07, Scrape(0.1, 1500, 2400, 0.45)), At(0.18, Clack(2200, 0.6)),
                    At(0.3, Scrape(0.08, 2400, 1500, 0.45)), At(0.4, Clack(1700, 1.0)), At(0.4, Thump(110, 28, 0.5)),
                });

            case SfxKind.rackPump:
                // The classic "chk-chk".
                return Mix(0.6, new[]
                {
                    At(0, Scrape(0.06, 900, 1600, 0.8, 0.7)), At(0.05, Clack(1300, 0.9)), At(0.05, Thump(90, 25, 0.7)),
                    At(0.2, Scrape(0.05, 1600, 900, 0.8, 0.7)), At(0.24, Clack(1100, 1.25)), At(0.24, Thump(80, 22, 0.9)),
                });

            case SfxKind.rackHeavy:
            {
                double phase = 0;
                var whir = Samples(0.75, u =>
                {
                    phase += 2 * Math.PI * (180 + 340 * Math.Min(u / 0.6, 1)) / Rate;
                    double motor = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 38 * u);
                    return (Math.Sin(phase) + 0.3 * Math.Sin(phase * 2)) * motor * Math.Min(1, u / 0.1) * Math.Exp(-Math.Max(0, u - 0.55) * 12) * 0.16;
                });
                return Mix(0.95, new[] { At(0, Thump(65, 10, 1.0)), At(0, Clack(900, 1.0)), At(0, Ring(600, 8, 0.28)), At(0.12, whir) });
            }

            case SfxKind.pinPull:
                return Mix(0.7, new[] { At(0, Clack(4200, 0.3)), At(0.02, Ring(3400, 9, 0.45)), At(0.22, Ring(5200, 18, 0.25)), At(0.22, Clack(2600, 0.25)) });

            case SfxKind.bladeDraw:
                return Mix(0.75, new[] { At(0, Scrape(0.28, 2500, 7500, 0.9, 0.5)), At(0.22, Ring(4100, 6, 0.33)), At(0.22, Ring(6200, 9, 0.18)) });

            case SfxKind.meleeSwing:
                return Mix(0.5, new[] { At(0, Whoosh(0.22, 500, 2200, 0.9)), At(0.22, Thump(110, 26, 0.7)), At(0.22, Clack(800, 0.4)) });

            default:
                return Clip(0.05, t => Click(t, 2300));
        }
    }

    private static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        int bytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + bytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write((int)Rate);
        w.Write((int)Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(bytes);
        foreach (float s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32000));
        w.Flush();
        return ms.ToArray();
    }
}
