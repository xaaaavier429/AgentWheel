using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using SD = System.Drawing;
using SDP = System.Drawing.Printing;

namespace AgentWheel;

// MARK: - Document model

public sealed class DocRun
{
    public string Text;
    public bool Bold;
    public bool Italic;

    public DocRun(string text, bool bold = false, bool italic = false)
    {
        Text = text;
        Bold = bold;
        Italic = italic;
    }
}

public sealed class DocPara
{
    public List<DocRun> Runs = new();
    /// <summary>0 for body text, 1–3 for headings.</summary>
    public int Heading;
    public bool Bullet;
    public bool Numbered;
    public bool Code;

    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>Paragraphs with headings, lists and bold / italic runs: what every built-in converter reads and writes.</summary>
public sealed class DocModel
{
    public List<DocPara> Paras = new();

    public string PlainText => string.Join("\n", Paras.Select(p => p.Text));

    public static DocModel FromText(string text)
    {
        var model = new DocModel();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var para = new DocPara();
            para.Runs.Add(new DocRun(line));
            model.Paras.Add(para);
        }
        return model;
    }
}

// MARK: - Office (Word / LibreOffice) for best fidelity

public static class Office
{
    private static bool? hasWord;

    public static bool HasWord => hasWord ??= Probe();

    private static bool Probe()
    {
        try
        {
            return OperatingSystem.IsWindows() && Type.GetTypeFromProgID("Word.Application") != null;
        }
        catch
        {
            return false;
        }
    }

    public static string? Soffice
    {
        get
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return Shell.Find("soffice");
        }
    }

    public static bool Available => HasWord || Soffice != null;

    public const string Hint = "需要 Microsoft Word 或免費的 LibreOffice 才能處理舊版 .doc";

    /// <summary>Converts with Word when installed, otherwise LibreOffice.</summary>
    public static async Task Convert(string input, string output, string fmt)
    {
        if (HasWord)
        {
            await Sta.Run(() => WordConvert(Path.GetFullPath(input), Path.GetFullPath(output), fmt));
            if (File.Exists(output)) return;
        }
        string? soffice = Soffice;
        if (soffice == null) throw new FWError(Hint);
        string tmp = Out.TempDir();
        try
        {
            string filter = fmt switch
            {
                "txt" => "txt:Text (encoded):UTF8",
                "html" => "html:HTML (StarWriter)",
                _ => fmt,
            };
            await Shell.Run(soffice, new[] { "--headless", "--norestore", "--convert-to", filter, "--outdir", tmp, input });
            string produced = Path.Combine(tmp, Path.GetFileNameWithoutExtension(input) + "." + fmt);
            if (!File.Exists(produced)) throw new FWError("LibreOffice 轉換失敗");
            File.Move(produced, output);
        }
        finally
        {
            Out.TryDelete(tmp);
        }
    }

    private static void WordConvert(string input, string output, string fmt)
    {
        int code = fmt switch
        {
            "doc" => 0,      // wdFormatDocument
            "txt" => 7,      // wdFormatUnicodeText
            "rtf" => 6,      // wdFormatRTF
            "html" => 10,    // wdFormatFilteredHTML
            "docx" => 16,    // wdFormatDocumentDefault
            "pdf" => 17,     // wdFormatPDF
            "odt" => 23,     // wdFormatOpenDocumentText
            _ => throw new FWError("Word 不支援輸出 " + fmt.ToUpperInvariant()),
        };
        Type type = Type.GetTypeFromProgID("Word.Application") ?? throw new FWError(Hint);
        dynamic word = Activator.CreateInstance(type)!;
        try
        {
            word.Visible = false;
            word.DisplayAlerts = 0;
            dynamic doc = word.Documents.Open(input, false, true, false);
            try
            {
                if (fmt == "txt") doc.SaveAs2(FileName: output, FileFormat: code, Encoding: 65001);
                else doc.SaveAs2(FileName: output, FileFormat: code);
            }
            finally
            {
                doc.Close(false);
            }
        }
        catch (FWError)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new FWError("Word 轉換失敗：" + Fmt.Trunc(e.Message, 80));
        }
        finally
        {
            try { word.Quit(false); } catch { }
            try { Marshal.FinalReleaseComObject(word); } catch { }
        }
    }
}

// MARK: - Converting

public static class DocOps
{
    public static bool IsMarkdown(string path) => Kinds.Ext(path) is "md" or "markdown";

    public static async Task<OpResult> Convert(string path, string fmt)
    {
        string source = Kinds.Ext(path);
        string output = Out.Target(path, null, fmt);
        if (IsMarkdown(path))
        {
            string markdown = DocReaders.ReadText(path);
            if (fmt == "txt") await File.WriteAllTextAsync(output, Markdown.Plain(markdown));
            else await DocWriters.Write(Markdown.ToModel(markdown), output, fmt);
            return new OpResult(output);
        }
        bool officeTarget = fmt is "pdf" or "doc" or "docx" or "odt" or "rtf" or "html";
        if (Office.Available && fmt != "md" && (officeTarget || source == "doc"))
        {
            try
            {
                await Office.Convert(path, output, fmt);
                return new OpResult(output);
            }
            catch (FWError) when (source != "doc")
            {
                // fall back to the built-in converter below
                Out.TryDelete(output);
            }
        }
        var model = await DocReaders.Read(path);
        await DocWriters.Write(model, output, fmt);
        return new OpResult(output);
    }

    public static async Task<string> PlainText(string path)
    {
        if (IsMarkdown(path)) return Markdown.Plain(DocReaders.ReadText(path));
        if (Kinds.Ext(path) is "txt" or "text") return DocReaders.ReadText(path);
        return (await DocReaders.Read(path)).PlainText;
    }

    public static async Task<OpResult> Count(string path)
    {
        string text = await PlainText(path);
        int cjk = 0, other = 0, words = 0;
        bool inWord = false;
        foreach (Rune r in text.EnumerateRunes())
        {
            int v = r.Value;
            bool isCjk = (v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF) || (v >= 0x20000 && v <= 0x2A6DF) || (v >= 0xF900 && v <= 0xFAFF);
            if (isCjk)
            {
                cjk++;
                inWord = false;
                continue;
            }
            if (Rune.IsLetterOrDigit(r))
            {
                if (!inWord) words++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
            if (!Rune.IsWhiteSpace(r)) other++;
        }
        var parts = new List<string> { "共 " + Fmt.Count(cjk + words) + " 字" };
        if (cjk > 0 && words > 0) parts.Add("中文 " + Fmt.Count(cjk) + " · 英文 " + Fmt.Count(words) + " 詞");
        parts.Add(Fmt.Count(cjk + other) + " 個字元（不含空格）");
        return new OpResult { Note = string.Join(" · ", parts) };
    }

    public static async Task<OpResult> Copy(string path)
    {
        string text = (await PlainText(path)).Trim();
        if (text.Length == 0) throw new FWError("文件裡沒有文字");
        return new OpResult { Note = "已複製 " + Fmt.Count(text.Length) + " 字", Clip = text };
    }
}

// MARK: - Readers

public static class DocReaders
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private const string FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    public static async Task<DocModel> Read(string path)
    {
        switch (Kinds.Ext(path))
        {
            case "md":
            case "markdown":
                return Markdown.ToModel(ReadText(path));
            case "html":
            case "htm":
                return Html(ReadText(path));
            case "docx":
                return Docx(path);
            case "odt":
                return Odt(path);
            case "rtf":
                return Rtf(path);
            case "doc":
            {
                string tmp = Out.TempDir();
                try
                {
                    string docx = Path.Combine(tmp, "doc.docx");
                    await Office.Convert(path, docx, "docx");
                    return Docx(docx);
                }
                finally
                {
                    Out.TryDelete(tmp);
                }
            }
            default:
                return DocModel.FromText(ReadText(path));
        }
    }

    /// <summary>Text files: UTF-8 / UTF-16 with BOM, UTF-8, then Big5, then GB18030.</summary>
    public static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        foreach (int codePage in new[] { 65001, 950, 54936 })
        {
            try
            {
                var strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return strict.GetString(bytes);
            }
            catch
            {
                // try the next encoding
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static DocPara NewPara(List<DocPara> into)
    {
        var p = new DocPara();
        into.Add(p);
        return p;
    }

    // Word (.docx)

    public static DocModel Docx(string path)
    {
        var model = new DocModel();
        using var zip = ZipFile.OpenRead(path);
        var body = zip.GetEntry("word/document.xml") ?? throw new FWError("這不是有效的 Word 文件");
        var headingStyles = new Dictionary<string, int>();
        var listStyles = new HashSet<string>();
        var stylesEntry = zip.GetEntry("word/styles.xml");
        if (stylesEntry != null)
        {
            var styles = new XmlDocument { XmlResolver = null };
            using (var s = stylesEntry.Open()) styles.Load(s);
            var sns = new XmlNamespaceManager(styles.NameTable);
            sns.AddNamespace("w", W);
            foreach (XmlElement style in styles.SelectNodes("//w:style", sns)!)
            {
                string id = style.GetAttribute("styleId", W);
                string name = ((style.SelectSingleNode("w:name", sns) as XmlElement)?.GetAttribute("val", W) ?? "").ToLowerInvariant();
                int level = 0;
                var m = Regex.Match(name, @"^heading\s*(\d)");
                if (m.Success) level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (name == "title") level = 1;
                else if (style.SelectSingleNode("w:pPr/w:outlineLvl", sns) is XmlElement outline &&
                         int.TryParse(outline.GetAttribute("val", W), out int lvl) && lvl < 9)
                    level = lvl + 1;
                if (level > 0) headingStyles[id] = Math.Min(3, level);
                else if (name.StartsWith("list") || style.SelectSingleNode("w:pPr/w:numPr", sns) != null) listStyles.Add(id);
            }
        }

        var doc = new XmlDocument { XmlResolver = null };
        using (var s = body.Open()) doc.Load(s);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("w", W);
        foreach (XmlElement p in doc.SelectNodes("//w:body//w:p", ns)!)
        {
            var para = NewPara(model.Paras);
            if (p.SelectSingleNode("w:pPr/w:pStyle", ns) is XmlElement ps && headingStyles.TryGetValue(ps.GetAttribute("val", W), out int h)) para.Heading = h;
            else if (p.SelectSingleNode("w:pPr/w:outlineLvl", ns) is XmlElement ol && int.TryParse(ol.GetAttribute("val", W), out int lv) && lv < 9) para.Heading = Math.Min(3, lv + 1);
            if (p.SelectSingleNode("w:pPr/w:numPr", ns) != null) para.Bullet = true;
            else if (p.SelectSingleNode("w:pPr/w:pStyle", ns) is XmlElement ls && listStyles.Contains(ls.GetAttribute("val", W))) para.Bullet = true;
            foreach (XmlElement r in p.SelectNodes(".//w:r", ns)!)
            {
                if (r.ParentNode is XmlElement parent && parent.LocalName == "del") continue;
                bool bold = IsOn(r.SelectSingleNode("w:rPr/w:b", ns) as XmlElement);
                bool italic = IsOn(r.SelectSingleNode("w:rPr/w:i", ns) as XmlElement);
                var text = new StringBuilder();
                foreach (XmlNode child in r.ChildNodes)
                {
                    switch (child.LocalName)
                    {
                        case "t": text.Append(child.InnerText); break;
                        case "tab": text.Append('\t'); break;
                        case "br":
                        case "cr": text.Append('\n'); break;
                        case "noBreakHyphen": text.Append('-'); break;
                    }
                }
                if (text.Length > 0) para.Runs.Add(new DocRun(text.ToString(), bold, italic));
            }
        }
        return model;
    }

    private static bool IsOn(XmlElement? flag)
    {
        if (flag == null) return false;
        string v = flag.GetAttribute("val", W);
        return v is not ("0" or "false" or "off");
    }

    // OpenDocument (.odt)

    public static DocModel Odt(string path)
    {
        var model = new DocModel();
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new FWError("這不是有效的 ODT 文件");
        var doc = new XmlDocument { XmlResolver = null };
        using (var s = entry.Open()) doc.Load(s);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("style", StyleNs);
        ns.AddNamespace("fo", FoNs);
        var boldStyles = new HashSet<string>();
        foreach (XmlElement style in doc.SelectNodes("//style:style", ns)!)
        {
            if (style.SelectSingleNode("style:text-properties", ns) is XmlElement props && props.GetAttribute("font-weight", FoNs) == "bold")
                boldStyles.Add(style.GetAttribute("name", StyleNs));
        }
        void Walk(XmlNode node, DocPara? para, bool bold, bool inList)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child is XmlText t)
                {
                    para?.Runs.Add(new DocRun(t.Value ?? "", bold));
                    continue;
                }
                if (child is not XmlElement e) continue;
                if (e.NamespaceURI != TextNs)
                {
                    Walk(e, para, bold, inList);
                    continue;
                }
                switch (e.LocalName)
                {
                    case "h":
                    {
                        var h = NewPara(model.Paras);
                        int.TryParse(e.GetAttribute("outline-level", TextNs), out int level);
                        h.Heading = Math.Clamp(level == 0 ? 1 : level, 1, 3);
                        Walk(e, h, bold, inList);
                        break;
                    }
                    case "p":
                    {
                        var p = NewPara(model.Paras);
                        p.Bullet = inList;
                        Walk(e, p, bold || boldStyles.Contains(e.GetAttribute("style-name", TextNs)), inList);
                        break;
                    }
                    case "list":
                        Walk(e, null, bold, true);
                        break;
                    case "span":
                        Walk(e, para, bold || boldStyles.Contains(e.GetAttribute("style-name", TextNs)), inList);
                        break;
                    case "s":
                        int.TryParse(e.GetAttribute("c", TextNs), out int count);
                        para?.Runs.Add(new DocRun(new string(' ', Math.Max(1, count))));
                        break;
                    case "tab":
                        para?.Runs.Add(new DocRun("\t"));
                        break;
                    case "line-break":
                        para?.Runs.Add(new DocRun("\n"));
                        break;
                    default:
                        Walk(e, para, bold, inList);
                        break;
                }
            }
        }
        var root = doc.DocumentElement;
        if (root != null) Walk(root, null, false, false);
        return model;
    }

    // HTML

    public static DocModel Html(string html)
    {
        var model = new DocModel();
        html = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        DocPara? para = null;
        int bold = 0, italic = 0, heading = 0;
        bool inLi = false, numbered = false;
        void Flush() => para = null;
        DocPara Current()
        {
            if (para != null) return para;
            para = NewPara(model.Paras);
            para.Heading = heading;
            para.Bullet = inLi && !numbered;
            para.Numbered = inLi && numbered;
            return para;
        }
        foreach (Match m in Regex.Matches(html, @"<(/?)([a-zA-Z0-9]+)[^>]*>|([^<]+)"))
        {
            if (m.Groups[3].Success)
            {
                string text = Regex.Replace(WebUtility.HtmlDecode(m.Groups[3].Value), @"\s+", " ");
                if (text.Trim().Length == 0 && para == null) continue;
                Current().Runs.Add(new DocRun(text, bold > 0 || heading > 0, italic > 0));
                continue;
            }
            bool closing = m.Groups[1].Value == "/";
            string tag = m.Groups[2].Value.ToLowerInvariant();
            switch (tag)
            {
                case "b":
                case "strong":
                    bold = Math.Max(0, bold + (closing ? -1 : 1));
                    break;
                case "i":
                case "em":
                    italic = Math.Max(0, italic + (closing ? -1 : 1));
                    break;
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    Flush();
                    heading = closing ? 0 : Math.Min(3, tag[1] - '0');
                    break;
                case "ol":
                    numbered = !closing;
                    Flush();
                    break;
                case "ul":
                    numbered = false;
                    Flush();
                    break;
                case "li":
                    inLi = !closing;
                    Flush();
                    break;
                case "br":
                    Current().Runs.Add(new DocRun("\n"));
                    break;
                case "p":
                case "div":
                case "tr":
                case "blockquote":
                case "pre":
                case "table":
                case "section":
                case "article":
                    Flush();
                    break;
            }
        }
        foreach (var p in model.Paras)
        {
            if (p.Runs.Count == 0) continue;
            p.Runs[0].Text = p.Runs[0].Text.TrimStart();
            p.Runs[^1].Text = p.Runs[^1].Text.TrimEnd();
        }
        model.Paras.RemoveAll(p => p.Text.Trim().Length == 0);
        return model;
    }

    // RTF

    public static DocModel Rtf(string path)
    {
        string rtf = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var model = new DocModel();
        var para = NewPara(model.Paras);
        var text = new StringBuilder();
        var pending = new List<byte>();
        int codePage = 1252;
        var stack = new Stack<(bool bold, bool italic, bool skip, int uc)>();
        bool bold = false, italic = false, skip = false;
        int uc = 1, skipChars = 0;
        var skipDestinations = new HashSet<string>
        {
            "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "footerl", "footerr",
            "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping", "latentstyles",
            "datastore", "fldinst", "object", "nonshppict", "mmathPr", "filetbl", "revtbl",
        };

        void FlushBytes()
        {
            if (pending.Count == 0) return;
            Encoding enc;
            try { enc = Encoding.GetEncoding(codePage); }
            catch { enc = Encoding.Latin1; }
            text.Append(enc.GetString(pending.ToArray()));
            pending.Clear();
        }
        void FlushRun()
        {
            FlushBytes();
            if (text.Length > 0)
            {
                para.Runs.Add(new DocRun(text.ToString(), bold, italic));
                text.Clear();
            }
        }
        void Emit(string s)
        {
            if (skip) return;
            if (skipChars > 0)
            {
                skipChars--;
                return;
            }
            FlushBytes();
            text.Append(s);
        }

        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{')
            {
                FlushRun();
                stack.Push((bold, italic, skip, uc));
                i++;
                if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*') skip = true;
                continue;
            }
            if (c == '}')
            {
                FlushRun();
                if (stack.Count > 0) (bold, italic, skip, uc) = stack.Pop();
                i++;
                continue;
            }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                char n = rtf[i];
                if (n == '\'' && i + 2 < rtf.Length)
                {
                    if (int.TryParse(rtf.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b) && !skip)
                    {
                        if (skipChars > 0) skipChars--;
                        else pending.Add((byte)b);
                    }
                    i += 3;
                    continue;
                }
                if (!char.IsLetter(n))
                {
                    switch (n)
                    {
                        case '\\':
                        case '{':
                        case '}': Emit(n.ToString()); break;
                        case '~': Emit("\u00A0"); break;
                        case '-': break;
                        case '\n':
                        case '\r':
                            if (!skip) { FlushRun(); para = NewPara(model.Paras); }
                            break;
                    }
                    i++;
                    continue;
                }
                int start = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                string word = rtf.Substring(start, i - start);
                int numStart = i;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                {
                    i++;
                    while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                }
                bool hasParam = i > numStart;
                int param = hasParam && int.TryParse(rtf.Substring(numStart, i - numStart), out int pv) ? pv : 0;
                if (i < rtf.Length && rtf[i] == ' ') i++;

                if (skipDestinations.Contains(word))
                {
                    skip = true;
                    continue;
                }
                if (word is "listtext" or "pntext")
                {
                    // The bullet or number drawn in front of a list item; the paragraph is a list item.
                    para.Bullet = true;
                    skip = true;
                    continue;
                }
                switch (word)
                {
                    case "ansicpg": codePage = param; break;
                    case "par":
                    case "line":
                    case "sect":
                    case "page":
                        if (!skip)
                        {
                            FlushRun();
                            para = NewPara(model.Paras);
                        }
                        break;
                    case "tab": Emit("\t"); break;
                    case "cell": Emit(" "); break;
                    case "row":
                        if (!skip)
                        {
                            FlushRun();
                            para = NewPara(model.Paras);
                        }
                        break;
                    case "b":
                        FlushRun();
                        bold = !hasParam || param != 0;
                        break;
                    case "i":
                        FlushRun();
                        italic = !hasParam || param != 0;
                        break;
                    case "plain":
                        FlushRun();
                        bold = false;
                        italic = false;
                        break;
                    case "uc": uc = param; break;
                    case "u":
                        if (!skip)
                        {
                            FlushBytes();
                            text.Append((char)(param < 0 ? param + 65536 : param));
                            skipChars = uc;
                        }
                        break;
                    case "emdash": Emit("—"); break;
                    case "endash": Emit("–"); break;
                    case "bullet": Emit("•"); break;
                    case "lquote": Emit("‘"); break;
                    case "rquote": Emit("’"); break;
                    case "ldblquote": Emit("“"); break;
                    case "rdblquote": Emit("”"); break;
                }
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                i++;
                continue;
            }
            if (!skip)
            {
                if (skipChars > 0) skipChars--;
                else
                {
                    FlushBytes();
                    text.Append(c);
                }
            }
            i++;
        }
        FlushRun();
        model.Paras.RemoveAll(p => p.Runs.Count == 0 && p != model.Paras.LastOrDefault());
        return model;
    }
}

// MARK: - Writers

public static class DocWriters
{
    public static async Task Write(DocModel model, string output, string fmt)
    {
        switch (fmt)
        {
            case "txt":
                await File.WriteAllTextAsync(output, model.PlainText, new UTF8Encoding(false));
                break;
            case "md":
                await File.WriteAllTextAsync(output, ToMarkdown(model), new UTF8Encoding(false));
                break;
            case "html":
                await File.WriteAllTextAsync(output, ToHtml(model), new UTF8Encoding(false));
                break;
            case "rtf":
                await File.WriteAllTextAsync(output, ToRtf(model), Encoding.ASCII);
                break;
            case "docx":
                WriteDocx(model, output);
                break;
            case "odt":
                WriteOdt(model, output);
                break;
            case "pdf":
                await PrintPdf(model, output);
                break;
            case "doc":
            {
                if (!Office.Available) throw new FWError(Office.Hint);
                string tmp = Out.TempDir();
                try
                {
                    string docx = Path.Combine(tmp, "doc.docx");
                    WriteDocx(model, docx);
                    await Office.Convert(docx, output, "doc");
                }
                finally
                {
                    Out.TryDelete(tmp);
                }
                break;
            }
            default:
                throw new FWError("不支援寫入 " + fmt.ToUpperInvariant());
        }
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string ToMarkdown(DocModel model)
    {
        var lines = new List<string>();
        int number = 0;
        DocPara? last = null;
        void Gap()
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
        }
        foreach (var p in model.Paras)
        {
            string plain = p.Text.Trim();
            if (plain.Length == 0)
            {
                Gap();
                number = 0;
                last = null;
                continue;
            }
            if (p.Heading > 0)
            {
                Gap();
                lines.Add(new string('#', p.Heading) + " " + plain);
                lines.Add("");
                number = 0;
                last = p;
                continue;
            }
            if (p.Code)
            {
                if (last == null || !last.Code) Gap();
                lines.Add("    " + p.Text);
                last = p;
                continue;
            }
            var sb = new StringBuilder();
            foreach (var r in p.Runs)
            {
                string t = r.Text;
                string core = t.Trim();
                if (core.Length == 0 || (!r.Bold && !r.Italic))
                {
                    sb.Append(t);
                    continue;
                }
                string mark = r.Bold ? "**" : "*";
                sb.Append(t.Substring(0, t.Length - t.TrimStart().Length)).Append(mark).Append(core).Append(mark)
                  .Append(t.Substring(t.TrimEnd().Length));
            }
            // A line break inside a paragraph becomes Markdown's hard break.
            string md = sb.ToString().Trim().Replace("\r\n", "\n").Replace("\n", "  \n");
            bool sameList = last != null && ((p.Bullet && last.Bullet) || (p.Numbered && last.Numbered));
            if (!sameList) Gap();
            if (p.Bullet) md = "- " + md;
            else if (p.Numbered) md = (++number) + ". " + md;
            if (!p.Numbered) number = 0;
            lines.Add(md);
            last = p;
        }
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines) + "\n";
    }

    private static string InlineHtml(DocPara p)
    {
        var sb = new StringBuilder();
        foreach (var r in p.Runs)
        {
            string t = Esc(r.Text).Replace("\n", "<br>");
            if (r.Bold) t = "<b>" + t + "</b>";
            if (r.Italic) t = "<i>" + t + "</i>";
            sb.Append(t);
        }
        return sb.ToString();
    }

    public static string ToHtml(DocModel model)
    {
        var html = new List<string>();
        string? list = null;
        void CloseList()
        {
            if (list != null) html.Add("</" + list + ">");
            list = null;
        }
        foreach (var p in model.Paras)
        {
            if (p.Text.Trim().Length == 0)
            {
                CloseList();
                continue;
            }
            if (p.Heading > 0)
            {
                CloseList();
                html.Add("<h" + p.Heading + ">" + Esc(p.Text.Trim()) + "</h" + p.Heading + ">");
            }
            else if (p.Code)
            {
                CloseList();
                html.Add("<pre><code>" + Esc(p.Text) + "</code></pre>");
            }
            else if (p.Bullet || p.Numbered)
            {
                string tag = p.Numbered ? "ol" : "ul";
                if (list != tag)
                {
                    CloseList();
                    html.Add("<" + tag + ">");
                    list = tag;
                }
                html.Add("<li>" + InlineHtml(p) + "</li>");
            }
            else
            {
                CloseList();
                html.Add("<p>" + InlineHtml(p) + "</p>");
            }
        }
        CloseList();
        const string style = "body{font-family:'Segoe UI','Microsoft JhengHei',sans-serif;font-size:15px;line-height:1.6;color:#111;max-width:760px;margin:40px auto;padding:0 16px}h1,h2,h3{margin:18px 0 8px}p{margin:0 0 10px}code,pre{font-family:Consolas,monospace;font-size:13px}";
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><style>" + style + "</style></head><body>\n" + string.Join("\n", html) + "\n</body></html>\n";
    }

    private static string RtfEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\line ");
            else if (c == '\t') sb.Append("\\tab ");
            else if (c > 127) sb.Append("\\u").Append((short)c).Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string ToRtf(DocModel model)
    {
        var sb = new StringBuilder();
        int number = 0;
        sb.Append("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl{\\f0\\fnil\\fcharset136 Microsoft JhengHei;}}\n");
        foreach (var p in model.Paras)
        {
            if (p.Heading > 0)
            {
                int size = p.Heading switch { 1 => 40, 2 => 32, _ => 27 };
                sb.Append("\\pard\\sb200\\sa120\\b\\fs").Append(size).Append(' ').Append(RtfEscape(p.Text.Trim())).Append("\\b0\\fs22\\par\n");
                continue;
            }
            sb.Append("\\pard\\sa120\\fs22 ");
            if (p.Bullet) sb.Append("\\u8226? ");
            if (p.Numbered) sb.Append(++number).Append(". ");
            else number = 0;
            foreach (var r in p.Runs)
            {
                if (r.Bold) sb.Append("\\b ");
                if (r.Italic) sb.Append("\\i ");
                sb.Append(RtfEscape(r.Text));
                if (r.Italic) sb.Append("\\i0 ");
                if (r.Bold) sb.Append("\\b0 ");
            }
            sb.Append("\\par\n");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AddEntry(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static void WriteDocx(DocModel model, string output)
    {
        var body = new StringBuilder();
        int number = 0;
        foreach (var p in model.Paras)
        {
            body.Append("<w:p>");
            if (p.Heading > 0) body.Append("<w:pPr><w:pStyle w:val=\"Heading").Append(p.Heading).Append("\"/></w:pPr>");
            string prefix = "";
            if (p.Bullet) prefix = "• ";
            else if (p.Numbered) prefix = (++number) + ". ";
            else number = 0;
            if (prefix.Length > 0) body.Append("<w:r><w:t xml:space=\"preserve\">").Append(Esc(prefix)).Append("</w:t></w:r>");
            foreach (var r in p.Runs)
            {
                var pieces = r.Text.Split('\n');
                for (int k = 0; k < pieces.Length; k++)
                {
                    body.Append("<w:r>");
                    if (r.Bold || r.Italic) body.Append("<w:rPr>").Append(r.Bold ? "<w:b/><w:bCs/>" : "").Append(r.Italic ? "<w:i/><w:iCs/>" : "").Append("</w:rPr>");
                    if (k > 0) body.Append("<w:br/>");
                    body.Append("<w:t xml:space=\"preserve\">").Append(Esc(pieces[k])).Append("</w:t></w:r>");
                }
            }
            body.Append("</w:p>");
        }
        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>";
        string HeadingStyle(int level, int halfPoints) =>
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading" + level + "\"><w:name w:val=\"heading " + level + "\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            "<w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"" + (level - 1) + "\"/></w:pPr>" +
            "<w:rPr><w:b/><w:bCs/><w:sz w:val=\"" + halfPoints + "\"/><w:szCs w:val=\"" + halfPoints + "\"/></w:rPr></w:style>";
        string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\" w:eastAsia=\"Microsoft JhengHei\" w:cs=\"Segoe UI\"/>" +
            "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"zh-TW\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            HeadingStyle(1, 40) + HeadingStyle(2, 32) + HeadingStyle(3, 27) +
            "</w:styles>";
        const string contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "</Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";
        const string documentRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";
        using var file = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        AddEntry(zip, "[Content_Types].xml", contentTypes);
        AddEntry(zip, "_rels/.rels", rels);
        AddEntry(zip, "word/document.xml", document);
        AddEntry(zip, "word/styles.xml", styles);
        AddEntry(zip, "word/_rels/document.xml.rels", documentRels);
    }

    public static void WriteOdt(DocModel model, string output)
    {
        var body = new StringBuilder();
        string? list = null;
        int number = 0;
        foreach (var p in model.Paras)
        {
            bool item = p.Bullet;
            number = p.Numbered ? number + 1 : 0;
            if (!item && list != null)
            {
                body.Append("</text:list>");
                list = null;
            }
            var inline = new StringBuilder();
            foreach (var r in p.Runs)
            {
                string t = Esc(r.Text).Replace("\n", "<text:line-break/>").Replace("\t", "<text:tab/>");
                if (r.Bold) inline.Append("<text:span text:style-name=\"B\">").Append(t).Append("</text:span>");
                else if (r.Italic) inline.Append("<text:span text:style-name=\"I\">").Append(t).Append("</text:span>");
                else inline.Append(t);
            }
            if (p.Heading > 0)
            {
                body.Append("<text:h text:style-name=\"H").Append(p.Heading).Append("\" text:outline-level=\"").Append(p.Heading).Append("\">")
                    .Append(Esc(p.Text.Trim())).Append("</text:h>");
            }
            else if (item)
            {
                if (list == null)
                {
                    body.Append("<text:list>");
                    list = "open";
                }
                body.Append("<text:list-item><text:p>").Append(inline).Append("</text:p></text:list-item>");
            }
            else
            {
                body.Append("<text:p>").Append(p.Numbered ? number + ". " : "").Append(inline).Append("</text:p>");
            }
        }
        if (list != null) body.Append("</text:list>");
        string HeadingStyle(int level, string size) =>
            "<style:style style:name=\"H" + level + "\" style:family=\"paragraph\"><style:text-properties fo:font-size=\"" + size +
            "\" fo:font-weight=\"bold\" style:font-size-asian=\"" + size + "\" style:font-weight-asian=\"bold\"/></style:style>";
        string content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
            "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" office:version=\"1.2\">" +
            "<office:automatic-styles>" +
            "<style:style style:name=\"B\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"/></style:style>" +
            "<style:style style:name=\"I\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\" style:font-style-asian=\"italic\"/></style:style>" +
            HeadingStyle(1, "20pt") + HeadingStyle(2, "16pt") + HeadingStyle(3, "13.5pt") +
            "</office:automatic-styles><office:body><office:text>" + body + "</office:text></office:body></office:document-content>";
        const string manifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>";
        using var file = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        // The mimetype entry must come first and uncompressed.
        AddEntry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        AddEntry(zip, "META-INF/manifest.xml", manifest);
        AddEntry(zip, "content.xml", content);
    }

    /// <summary>
    /// Built-in PDF: lays the text out on A4 and prints it through Windows' own "Microsoft Print to PDF".
    /// (When Word or LibreOffice is installed, DocOps uses them instead for an exact layout.)
    /// </summary>
    public static Task PrintPdf(DocModel model, string output) => Sta.Run(() =>
    {
        const string printer = "Microsoft Print to PDF";
        bool installed = SDP.PrinterSettings.InstalledPrinters.Cast<string>().Any(p => p == printer);
        if (!installed) throw new FWError("這台電腦沒有「Microsoft Print to PDF」：請在 Windows 設定開啟它，或安裝 Word / LibreOffice");

        var lines = model.Paras.Select(p => (
            text: (p.Bullet ? "• " : "") + p.Text,
            size: p.Heading switch { 1 => 20f, 2 => 16f, 3 => 13.5f, _ => 11f },
            bold: p.Heading > 0 || (p.Runs.Count > 0 && p.Runs.All(r => r.Bold || r.Text.Trim().Length == 0)))).ToList();
        int index = 0, offset = 0;
        using var doc = new SDP.PrintDocument();
        doc.PrinterSettings.PrinterName = printer;
        doc.PrinterSettings.PrintToFile = true;
        doc.PrinterSettings.PrintFileName = Path.GetFullPath(output);
        doc.DefaultPageSettings.PaperSize = new SDP.PaperSize("A4", 827, 1169);
        doc.DefaultPageSettings.Margins = new SDP.Margins(83, 83, 78, 78);
        doc.PrintController = new SDP.StandardPrintController();
        doc.DocumentName = Path.GetFileNameWithoutExtension(output);
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            var bounds = e.MarginBounds;
            float y = bounds.Top;
            using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic) { Trimming = SD.StringTrimming.Word };
            format.FormatFlags |= SD.StringFormatFlags.LineLimit;
            while (index < lines.Count)
            {
                var (text, size, bold) = lines[index];
                string rest = text.Substring(Math.Min(offset, text.Length));
                using var font = new SD.Font("Microsoft JhengHei", size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular, SD.GraphicsUnit.Point);
                float lineHeight = font.GetHeight(g);
                if (rest.Trim().Length == 0)
                {
                    y += lineHeight * 0.6f;
                    index++;
                    offset = 0;
                    continue;
                }
                var room = new SD.SizeF(bounds.Width, bounds.Bottom - y);
                g.MeasureString(rest, font, room, format, out int chars, out int _);
                if (chars <= 0)
                {
                    e.HasMorePages = true;
                    return;
                }
                string piece = rest.Substring(0, chars);
                var used = g.MeasureString(piece, font, bounds.Width, format);
                g.DrawString(piece, font, SD.Brushes.Black, new SD.RectangleF(bounds.Left, y, bounds.Width, used.Height + 2), format);
                y += used.Height + (size > 11 ? lineHeight * 0.6f : lineHeight * 0.35f);
                if (chars < rest.Length)
                {
                    offset += chars;
                    e.HasMorePages = true;
                    return;
                }
                index++;
                offset = 0;
            }
            e.HasMorePages = false;
        };
        doc.Print();
        if (!File.Exists(output)) throw new FWError("產生 PDF 失敗");
    });
}

// MARK: - Markdown

/// <summary>Just enough Markdown for documents: headings, emphasis, code, lists, quotes, links.</summary>
public static class Markdown
{
    private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|__[^_]+__|`[^`]+`|!\[[^\]]*\]\([^)]+\)|\[[^\]]+\]\([^)]+\)|(?<![*\w])\*[^*\s][^*]*\*(?!\*))");

    private static int? HeadingLevel(string line)
    {
        int hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes < 1 || hashes > 6 || line.Length <= hashes || line[hashes] != ' ') return null;
        return hashes;
    }

    private static string? Bullet(string line)
    {
        foreach (var marker in new[] { "- ", "* ", "+ ", "• " })
            if (line.StartsWith(marker)) return line.Substring(marker.Length);
        return null;
    }

    private static string? Numbered(string line)
    {
        var m = Regex.Match(line, @"^\d+[.)] (.*)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<DocRun> Runs(string text)
    {
        var runs = new List<DocRun>();
        int last = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > last) runs.Add(new DocRun(text.Substring(last, m.Index - last)));
            string token = m.Value;
            if (token.StartsWith("**") || token.StartsWith("__")) runs.Add(new DocRun(token.Substring(2, token.Length - 4), true));
            else if (token.StartsWith("`")) runs.Add(new DocRun(token.Substring(1, token.Length - 2)));
            else if (token.StartsWith("![")) runs.Add(new DocRun(Regex.Match(token, @"!\[([^\]]*)\]").Groups[1].Value));
            else if (token.StartsWith("[")) runs.Add(new DocRun(Regex.Match(token, @"\[([^\]]+)\]").Groups[1].Value));
            else runs.Add(new DocRun(token.Substring(1, token.Length - 2), false, true));
            last = m.Index + m.Length;
        }
        if (last < text.Length) runs.Add(new DocRun(text.Substring(last)));
        return runs;
    }

    public static DocModel ToModel(string source)
    {
        var model = new DocModel();
        DocPara? paragraph = null;
        bool inCode = false;
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```"))
            {
                inCode = !inCode;
                paragraph = null;
                continue;
            }
            if (inCode)
            {
                var code = new DocPara { Code = true };
                code.Runs.Add(new DocRun(raw));
                model.Paras.Add(code);
                continue;
            }
            if (line.Length == 0)
            {
                paragraph = null;
                continue;
            }
            if (HeadingLevel(line) is int level)
            {
                var h = new DocPara { Heading = Math.Min(3, level) };
                h.Runs.Add(new DocRun(line.TrimStart('#').Trim()));
                model.Paras.Add(h);
                paragraph = null;
            }
            else if (line is "---" or "***" or "___")
            {
                paragraph = null;
            }
            else if (line.StartsWith(">"))
            {
                var q = new DocPara();
                q.Runs.AddRange(Runs(line.Substring(1).Trim()));
                model.Paras.Add(q);
                paragraph = null;
            }
            else if (Bullet(line) is string item)
            {
                var b = new DocPara { Bullet = true };
                b.Runs.AddRange(Runs(item));
                model.Paras.Add(b);
                paragraph = null;
            }
            else if (Numbered(line) is string numbered)
            {
                var n = new DocPara { Numbered = true };
                n.Runs.AddRange(Runs(numbered));
                model.Paras.Add(n);
                paragraph = null;
            }
            else if (paragraph != null)
            {
                paragraph.Runs.Add(new DocRun("\n"));
                paragraph.Runs.AddRange(Runs(line));
            }
            else
            {
                paragraph = new DocPara();
                paragraph.Runs.AddRange(Runs(line));
                model.Paras.Add(paragraph);
            }
        }
        return model;
    }

    public static string Plain(string source) => string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Select(raw =>
    {
        string line = raw;
        if (HeadingLevel(line.Trim()) != null) line = line.Trim().TrimStart('#').Trim();
        line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]+\)", "$1");
        line = Regex.Replace(line, @"(?<![*\w])\*([^*\s][^*]*)\*(?!\*)", "$1");
        foreach (var token in new[] { "**", "__", "`" }) line = line.Replace(token, "");
        return line;
    }));
}
