using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;

namespace AgentWheel;

public sealed class RunSummary
{
    public List<string> Outputs = new();
    public string Title = "";
    public string Detail = "";
    /// <summary>Total bytes of the source files and of the results (0 when not measured).</summary>
    public long Before;
    public long After;

    /// <summary>Display name of the folder the results went to ("桌面", "下載"…).</summary>
    public string? Saved => Outputs.Count > 0 ? Out.FolderName(Outputs[0]) : null;
}

public static class Engine
{
    private static string SavedDetail(string output) =>
        Path.GetFileName(output) + " · " + Fmt.Bytes(Fmt.Size(output)) + " · 存到「" + Out.FolderName(output) + "」";

    /// <summary>Runs one wheel option over the dragged files.</summary>
    public static async Task<RunSummary> Run(string id, string label, IList<string> files, ProgressFn progress)
    {
        string name = files.Count == 1 ? Path.GetFileName(files[0]) : files.Count + " 個檔案";
        string title = name + "  →  " + label;

        // Actions that take the whole selection at once.
        if (id == "pdf.merge" || (id == "img.to.pdf" && files.Count > 1))
        {
            string output = await Task.Run(() => PdfOps.Merge(files));
            return new RunSummary { Outputs = { output }, Title = title, Detail = SavedDetail(output) };
        }
        switch (id)
        {
            case "gen.zip":
            {
                string output = await Task.Run(() => GenericOps.Zip(files));
                return new RunSummary { Outputs = { output }, Title = title, Detail = SavedDetail(output) };
            }
            case "gen.path":
                await ClipboardText.Set(string.Join("\r\n", files));
                return new RunSummary { Title = title, Detail = files.Count == 1 ? files[0] : files.Count + " 條路徑已複製" };
            case "gen.name":
                await ClipboardText.Set(string.Join("\r\n", files.Select(Path.GetFileName)));
                return new RunSummary { Title = title, Detail = "已複製到剪貼簿" };
        }

        var kind = Catalog.Context(files).Kind;
        var targets = files.Where(f => Kinds.Of(f) == kind || id.StartsWith("gen.")).ToList();
        var outputs = new List<string>();
        var notes = new List<string>();
        var clips = new List<string>();
        var failures = new List<Exception>();
        long before = 0, after = 0;
        for (int n = 0; n < targets.Count; n++)
        {
            string file = targets[n];
            int index = n;
            ProgressFn step = p => progress((index + p) / targets.Count);
            try
            {
                var result = await Task.Run(() => RunOne(id, file, step));
                outputs.AddRange(result.Outputs);
                if (result.Note != null) notes.Add(result.Note);
                if (result.Clip != null) clips.Add(result.Clip);
                before += Fmt.Size(file);
                after += result.Outputs.Sum(o => Fmt.Size(o));
            }
            catch (Exception e)
            {
                Log.Write("run: " + file + " failed: " + e);
                failures.Add(e);
            }
        }
        if (outputs.Count == 0 && notes.Count == 0 && clips.Count == 0)
        {
            var first = failures.FirstOrDefault();
            if (first is FWError) throw first;
            if (first != null) throw new FWError(Fmt.Trunc(first.Message, 120));
            throw new FWError("沒有可以處理的檔案");
        }
        if (clips.Count > 0) await ClipboardText.Set(string.Join("\r\n\r\n", clips));

        var detail = new List<string>();
        if (outputs.Count > 0 && after > 0 && !id.StartsWith("gen.")) detail.Add(Fmt.Bytes(before) + " → " + Fmt.Bytes(after));
        if (targets.Count == 1 && notes.Count > 0)
        {
            detail.Add(notes[0]);
        }
        else if (notes.Count > 0 && id.EndsWith("compress"))
        {
            int saved = (int)((1 - (double)after / Math.Max(before, 1)) * 100);
            detail.Add("體積 -" + saved + "%");
        }
        else if (clips.Count > 0)
        {
            detail.Add("文字已複製到剪貼簿");
        }
        if (failures.Count > 0) detail.Add(failures.Count + " 個失敗：" + Fmt.Trunc(failures[0].Message, 60));
        var summary = new RunSummary { Outputs = outputs, Title = title, Before = before, After = after };
        if (summary.Saved is string folder) detail.Add("存到「" + folder + "」");
        summary.Detail = string.Join(" · ", detail);
        return summary;
    }

    public static async Task<OpResult> RunOne(string id, string file, ProgressFn progress)
    {
        var parts = id.Split('.');
        if (parts.Length < 2) throw new FWError("未知操作 " + id);
        string? target = parts.Length == 3 && parts[1] == "to" ? parts[2] : null;

        switch (parts[0])
        {
            case "img":
                if (target != null) return await ImageOps.Convert(file, target);
                switch (id)
                {
                    case "img.compress": return await ImageOps.Compress(file);
                    case "img.half": return await ImageOps.Half(file);
                    case "img.cutout": return await ImageOps.Cutout(file);
                    case "img.ocr": return await ImageOps.Ocr(file);
                    case "img.mono": return await ImageOps.Mono(file);
                    case "img.rotate": return await ImageOps.Rotate(file);
                    case "img.strip": return await ImageOps.Strip(file);
                    case "img.backdrop": return await ImageOps.Backdrop(file);
                }
                break;
            case "pdf":
                switch (id)
                {
                    case "pdf.to.png": return await PdfOps.ToImages(file, "png", progress);
                    case "pdf.to.jpg": return await PdfOps.ToImages(file, "jpg", progress);
                    case "pdf.to.long": return await PdfOps.LongImage(file, progress);
                    case "pdf.compress": return PdfOps.Compress(file);
                    case "pdf.split": return PdfOps.Split(file);
                    case "pdf.rotate": return PdfOps.Rotate(file);
                    case "pdf.mono": return await PdfOps.Mono(file, progress);
                    case "pdf.ocr": return await PdfOps.Ocr(file, progress);
                    case "pdf.strip": return Privacy.StripPdf(file);
                    default:
                        if (target != null) return await PdfOps.ToText(file, target);
                        break;
                }
                break;
            case "vid":
                if (id == "vid.strip") return await Privacy.StripVideo(file, progress);
                return await MediaOps.Video(id, file, progress);
            case "aud":
                return await MediaOps.Audio(id, file, progress);
            case "doc":
                if (target != null) return await DocOps.Convert(file, target);
                switch (id)
                {
                    case "doc.count": return await DocOps.Count(file);
                    case "doc.copy": return await DocOps.Copy(file);
                    case "doc.strip": return Privacy.StripDocx(file);
                }
                break;
            case "gen":
                if (id == "gen.sha") return GenericOps.Sha256(file);
                break;
        }
        throw new FWError("未知操作 " + id);
    }
}

public static class GenericOps
{
    public static string Zip(IList<string> files)
    {
        if (files.Count == 0) throw new FWError("沒有檔案");
        string first = files[0];
        if (files.Count == 1)
        {
            string output = Out.Target(first, null, "zip");
            if (Directory.Exists(first)) ZipFile.CreateFromDirectory(first, output, CompressionLevel.Optimal, true);
            else
            {
                using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(first, Path.GetFileName(first), CompressionLevel.Optimal);
            }
            return output;
        }
        string multi = Out.Unique(Out.Dir(first), "歸檔 " + files.Count + " 項", "zip");
        using (var zip = ZipFile.Open(multi, ZipArchiveMode.Create))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string UniqueName(string name)
            {
                string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                string candidate = name;
                for (int n = 2; !used.Add(candidate); n++) candidate = stem + " " + n + ext;
                return candidate;
            }
            foreach (var file in files)
            {
                if (Directory.Exists(file))
                {
                    string root = "歸檔/" + UniqueName(Path.GetFileName(file.TrimEnd('\\', '/')));
                    foreach (var path in Directory.EnumerateFiles(file, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(file, path).Replace('\\', '/');
                        zip.CreateEntryFromFile(path, root + "/" + relative, CompressionLevel.Optimal);
                    }
                }
                else if (File.Exists(file))
                {
                    zip.CreateEntryFromFile(file, "歸檔/" + UniqueName(Path.GetFileName(file)), CompressionLevel.Optimal);
                }
            }
        }
        return multi;
    }

    public static OpResult Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        string hex = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new OpResult { Note = "SHA-256 " + hex.Substring(0, 12) + "… 已複製", Clip = hex };
    }
}

/// <summary>The handling sound played when a slot's action succeeds, picked per action so each format has its own feel.</summary>
public static class EquipSound
{
    public static SfxKind Of(string id)
    {
        if (Table.TryGetValue(id, out var hit)) return hit;
        if (id.EndsWith(".compress")) return SfxKind.pinPull;
        return SfxKind.rackPistol;
    }

    private static readonly Dictionary<string, SfxKind> Table = new()
    {
        ["img.to.png"] = SfxKind.rackRifle, ["img.to.jpg"] = SfxKind.rackPistol, ["img.to.heic"] = SfxKind.rackPistol, ["img.to.webp"] = SfxKind.rackPistol,
        ["img.to.pdf"] = SfxKind.rackHeavy, ["img.to.avif"] = SfxKind.rackBolt, ["img.to.gif"] = SfxKind.rackPump, ["img.to.tiff"] = SfxKind.rackHeavy,
        ["img.half"] = SfxKind.meleeSwing, ["img.cutout"] = SfxKind.bladeDraw, ["img.ocr"] = SfxKind.meleeSwing, ["img.mono"] = SfxKind.meleeSwing,
        ["img.rotate"] = SfxKind.meleeSwing, ["img.backdrop"] = SfxKind.meleeSwing, ["pdf.to.png"] = SfxKind.rackRifle, ["pdf.to.jpg"] = SfxKind.rackPistol,
        ["pdf.to.long"] = SfxKind.rackBolt, ["pdf.to.docx"] = SfxKind.rackPump, ["pdf.to.txt"] = SfxKind.meleeSwing, ["pdf.to.md"] = SfxKind.rackPistol,
        ["pdf.to.rtf"] = SfxKind.rackPistol, ["pdf.to.html"] = SfxKind.rackHeavy, ["pdf.split"] = SfxKind.bladeDraw, ["pdf.merge"] = SfxKind.pinPull,
        ["pdf.rotate"] = SfxKind.meleeSwing, ["pdf.mono"] = SfxKind.meleeSwing, ["pdf.ocr"] = SfxKind.meleeSwing, ["vid.to.mp4"] = SfxKind.rackRifle,
        ["vid.to.mov"] = SfxKind.rackPistol, ["vid.to.hevc"] = SfxKind.rackBolt, ["vid.to.webm"] = SfxKind.rackPistol, ["vid.to.gif"] = SfxKind.rackPump,
        ["vid.to.mp3"] = SfxKind.rackPistol, ["vid.to.m4a"] = SfxKind.pinPull, ["vid.to.wav"] = SfxKind.rackHeavy, ["vid.mute"] = SfxKind.rackPistol,
        ["vid.720p"] = SfxKind.meleeSwing, ["vid.cover"] = SfxKind.meleeSwing, ["vid.fast"] = SfxKind.rackPistol, ["vid.slow"] = SfxKind.rackBolt,
        ["vid.strip"] = SfxKind.pinPull, ["aud.to.mp3"] = SfxKind.rackPistol, ["aud.to.m4a"] = SfxKind.rackPistol, ["aud.to.wav"] = SfxKind.rackHeavy,
        ["aud.to.flac"] = SfxKind.rackRifle, ["aud.to.aiff"] = SfxKind.rackPump, ["aud.to.opus"] = SfxKind.rackBolt, ["aud.to.alac"] = SfxKind.rackPistol,
        ["aud.to.m4r"] = SfxKind.pinPull, ["aud.mono"] = SfxKind.bladeDraw, ["aud.loud"] = SfxKind.meleeSwing, ["aud.fast"] = SfxKind.rackPistol,
        ["doc.to.pdf"] = SfxKind.rackHeavy, ["doc.to.docx"] = SfxKind.rackRifle, ["doc.to.txt"] = SfxKind.meleeSwing, ["doc.to.md"] = SfxKind.rackPistol,
        ["doc.to.html"] = SfxKind.pinPull, ["doc.to.rtf"] = SfxKind.rackPistol, ["doc.to.odt"] = SfxKind.rackPump, ["doc.to.doc"] = SfxKind.rackPistol,
        ["doc.count"] = SfxKind.meleeSwing, ["doc.copy"] = SfxKind.bladeDraw, ["gen.zip"] = SfxKind.rackHeavy, ["gen.path"] = SfxKind.meleeSwing,
        ["gen.name"] = SfxKind.bladeDraw, ["gen.sha"] = SfxKind.meleeSwing,
    };
}

/// <summary>Runs a chosen slot in the background and reports back with the HUD and banner.</summary>
public sealed class Runner
{
    public static readonly Runner Shared = new();
    private static readonly Random Dice = new();

    public async void Run(WheelOption option, IList<string> files, Native.MonitorInfo? monitor, Wanted? wanted = null)
    {
        wanted ??= new Wanted();
        string name = files.Count == 1 ? Path.GetFileName(files[0]) : files.Count + " 個檔案";
        var job = BusyHud.Shared.Begin(name + " → " + option.Title);
        string id = option.Id, label = option.Title;
        var dispatcher = Application.Current.Dispatcher;
        Log.Write("run: " + id + " on " + files.Count + " file(s)");
        try
        {
            var summary = await Task.Run(() => Engine.Run(id, label, files.ToList(), p =>
                dispatcher.InvokeAsync(() => BusyHud.Shared.Update(job, p))));
            Log.Write("run: ok → " + string.Join(", ", summary.Outputs) + " " + summary.Detail);
            BusyHud.Shared.End(job);
            Settings.AddRecents(summary.Outputs);
            string detail = summary.Detail;
            bool evaded = id.EndsWith(".strip") && wanted.Level > 0;
            var outcome = Outcomes.Pick(files.Count, summary.Before, summary.After, evaded);
            // An equip sound first, then the round-win (or ACE / cleared-reveal) sound.
            SFX.Shared.Play(EquipSound.Of(id));
            SFX.Shared.Play(outcome.Sound(), 0.3);
            if (evaded)
            {
                WantedHud.Shared.Evade(wanted, monitor);
                detail = "偵查 " + new string('◆', wanted.Level) + " 已解除" + (summary.Saved is string s ? " · 存到「" + s + "」" : "");
            }
            BannerController.Shared.Show(true, summary.Title, detail, monitor, outcome);
        }
        catch (Exception e)
        {
            Log.Write("run: failed " + id + ": " + e);
            BusyHud.Shared.End(job);
            SFX.Shared.Play(SfxKind.wasted);
            string message = e is FWError ? e.Message : Fmt.Trunc(e.Message, 120);
            BannerController.Shared.Show(false, name + "  →  " + label, message, monitor);
        }
    }

    /// <summary>Demo wheel: nothing is converted, it only plays the finish.</summary>
    public void Demo(WheelOption option, WheelContext context, Native.MonitorInfo? monitor)
    {
        var choices = new[] { Outcome.victory, Outcome.ace, Outcome.thrifty, Outcome.flawless };
        var outcome = choices[Dice.Next(choices.Length)];
        SFX.Shared.Play(EquipSound.Of(option.Id));
        SFX.Shared.Play(outcome.Sound(), 0.3);
        BannerController.Shared.Show(true, context.Headline + "  →  " + option.Title,
            "示範模式 · 拖一個真檔案再按 Shift 試試", monitor, outcome);
    }
}
