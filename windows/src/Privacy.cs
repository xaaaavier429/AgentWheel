using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AgentWheel;

/// <summary>How much of a file can be traced back to you (0…5 red markers) and why.</summary>
public sealed class Wanted
{
    public int Level;
    public List<string> Reasons = new();
}

public static class Privacy
{
    public const string EvadedNote = "已解除偵查：定位、裝置和作者資訊都抹掉了";

    // MARK: - Scanning

    public static async Task<Wanted> Scan(IList<string> files)
    {
        var worst = new Wanted();
        int flagged = 0;
        foreach (var path in files.Take(24))
        {
            var found = await Scan(path);
            if (found.Level > 0) flagged++;
            if (found.Level > worst.Level) worst = found;
        }
        if (flagged > 1) worst.Reasons.Add(flagged + " 個檔案留有痕跡");
        return worst;
    }

    public static async Task<Wanted> Scan(string path)
    {
        Wanted found;
        try
        {
            found = Kinds.Of(path) switch
            {
                FileKind.image => Image(path),
                FileKind.video => await Video(path),
                FileKind.pdf => Pdf(path),
                FileKind.document => Kinds.Ext(path) == "docx" ? Docx(path) : new Wanted(),
                _ => new Wanted(),
            };
        }
        catch
        {
            found = new Wanted();
        }
        found.Level = Math.Min(found.Level, 5);
        return found;
    }

    private static string? NonEmpty(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static Wanted Image(string path)
    {
        var found = new Wanted();
        var dirs = ImageMetadataReader.ReadMetadata(path);
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (gps != null && gps.ContainsTag(GpsDirectory.TagLatitude))
        {
            found.Level += 2;
            found.Reasons.Add("GPS 定位");
        }
        string? model = NonEmpty(ifd0?.GetDescription(ExifDirectoryBase.TagModel)) ?? NonEmpty(sub?.GetDescription(ExifDirectoryBase.TagLensModel));
        if (model != null)
        {
            found.Level += 1;
            found.Reasons.Add(Fmt.Trunc(model, 24));
        }
        if (sub != null && sub.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal))
        {
            found.Level += 1;
            found.Reasons.Add("拍攝時間");
        }
        if (NonEmpty(sub?.GetDescription(ExifDirectoryBase.TagBodySerialNumber)) != null
            || NonEmpty(sub?.GetDescription(ExifDirectoryBase.TagCameraOwnerName)) != null
            || NonEmpty(ifd0?.GetDescription(ExifDirectoryBase.TagArtist)) != null)
        {
            found.Level += 1;
            found.Reasons.Add("機身序號 / 作者");
        }
        return found;
    }

    /// <summary>Tags as ffprobe sees them.</summary>
    public static async Task<Wanted> Video(string path)
    {
        var found = new Wanted();
        string? probe = Shell.Ffprobe;
        if (probe == null) return found;
        string text;
        try
        {
            text = await Shell.Run(probe, new[] { "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "default=nw=1", path });
        }
        catch
        {
            return found;
        }
        var tags = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("TAG:")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            tags[line.Substring(4, eq - 4).ToLowerInvariant()] = line.Substring(eq + 1);
        }
        if (tags.Keys.Any(k => k.Contains("location") || k == "©xyz"))
        {
            found.Level += 2;
            found.Reasons.Add("GPS 定位");
        }
        var model = tags.FirstOrDefault(t => t.Key.EndsWith("model")).Value;
        if (!string.IsNullOrWhiteSpace(model))
        {
            found.Level += 1;
            found.Reasons.Add(Fmt.Trunc(model, 24));
        }
        // creation_time exists in almost every file; only a camera capture date counts.
        if (tags.ContainsKey("com.apple.quicktime.creationdate"))
        {
            found.Level += 1;
            found.Reasons.Add("拍攝時間");
        }
        return found;
    }

    public static Wanted Pdf(string path)
    {
        var found = new Wanted();
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        string? author = NonEmpty(doc.Info.Author);
        if (author != null)
        {
            found.Level += 2;
            found.Reasons.Add("作者：" + Fmt.Trunc(author, 16));
        }
        if (NonEmpty(doc.Info.Creator) != null)
        {
            found.Level += 1;
            found.Reasons.Add("製作軟體");
        }
        if (NonEmpty(doc.Info.Keywords) != null || NonEmpty(doc.Info.Subject) != null)
        {
            found.Level += 1;
            found.Reasons.Add("主題 / 關鍵字");
        }
        // Dates are left out on purpose: saving any PDF stamps a fresh one, so they aren't a trail.
        return found;
    }

    private static string? ReadEntry(string zipPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? Tag(string name, string xml)
    {
        var m = Regex.Match(xml, "<" + Regex.Escape(name) + "[^>]*>([^<]*)</" + Regex.Escape(name) + ">");
        return m.Success ? NonEmpty(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)) : null;
    }

    public static Wanted Docx(string path)
    {
        var found = new Wanted();
        string core = ReadEntry(path, "docProps/core.xml") ?? "";
        string? creator = Tag("dc:creator", core);
        if (creator != null)
        {
            found.Level += 2;
            found.Reasons.Add("作者：" + Fmt.Trunc(creator, 16));
        }
        string? editor = Tag("cp:lastModifiedBy", core);
        if (editor != null && editor != creator)
        {
            found.Level += 1;
            found.Reasons.Add("修改人：" + Fmt.Trunc(editor, 16));
        }
        if (Tag("Company", ReadEntry(path, "docProps/app.xml") ?? "") != null)
        {
            found.Level += 1;
            found.Reasons.Add("公司名稱");
        }
        return found;
    }

    // MARK: - 去隱私

    /// <summary>Videos: re-wrap without re-encoding and without metadata (location, device, dates).</summary>
    public static async Task<OpResult> StripVideo(string path, ProgressFn progress)
    {
        string ext = Kinds.Ext(path);
        string output = Out.Target(path, "去隱私", ext.Length == 0 ? "mp4" : ext);
        await MediaOps.Ff(path, new[] { "-map", "0", "-map_metadata", "-1", "-c", "copy" }, output, progress);
        return new OpResult(output) { Note = EvadedNote };
    }

    public static OpResult StripPdf(string path)
    {
        string output = Out.Target(path, "去隱私", "pdf");
        using (var doc = PdfOps.OpenForEdit(path))
        {
            foreach (var key in new[] { "/Author", "/Creator", "/Producer", "/Subject", "/Keywords", "/Title", "/CreationDate", "/ModDate" })
                doc.Info.Elements.Remove(key);
            doc.Internals.Catalog.Elements.Remove("/Metadata");
            doc.Save(output);
        }
        return new OpResult(output) { Note = EvadedNote };
    }

    /// <summary>Word: blanks the author fields in docProps/core.xml and app.xml inside a copy of the file.</summary>
    public static OpResult StripDocx(string path)
    {
        string output = Out.Target(path, "去隱私", "docx");
        File.Copy(path, output);
        try
        {
            var fields = new Dictionary<string, string[]>
            {
                ["docProps/core.xml"] = new[] { "dc:creator", "cp:lastModifiedBy", "cp:keywords", "dc:description", "dc:subject", "cp:category" },
                ["docProps/app.xml"] = new[] { "Company", "Manager", "Template" },
            };
            int changed = 0;
            using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                foreach (var (entryName, names) in fields)
                {
                    var entry = zip.GetEntry(entryName);
                    if (entry == null) continue;
                    string xml;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) xml = reader.ReadToEnd();
                    string cleaned = xml;
                    foreach (var name in names)
                    {
                        string n = Regex.Escape(name);
                        cleaned = Regex.Replace(cleaned, "<" + n + "([^>]*)>[^<]*</" + n + ">", "<" + name + "$1></" + name + ">");
                    }
                    entry.Delete();
                    var fresh = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(fresh.Open(), new UTF8Encoding(false))) writer.Write(cleaned);
                    changed++;
                }
            }
            if (changed == 0) throw new FWError("這個文件裡沒有作者資訊");
        }
        catch
        {
            Out.TryDelete(output);
            throw;
        }
        return new OpResult(output) { Note = EvadedNote };
    }
}
