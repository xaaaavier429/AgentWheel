using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace AgentWheel;

/// <summary>Images through Magick.NET (PNG, JPG, HEIC, WebP, AVIF, GIF, TIFF, BMP, RAW…).</summary>
public static class ImageOps
{
    private static readonly Dictionary<string, MagickFormat> Writers = new()
    {
        ["png"] = MagickFormat.Png,
        ["jpg"] = MagickFormat.Jpeg,
        ["heic"] = MagickFormat.Heic,
        ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif,
        ["gif"] = MagickFormat.Gif,
        ["tiff"] = MagickFormat.Tiff,
        ["bmp"] = MagickFormat.Bmp,
    };

    // MARK: - Reading and writing

    /// <summary>Decodes the primary image with its EXIF orientation already applied.</summary>
    public static MagickImage Load(string path)
    {
        try
        {
            var image = new MagickImage(path);
            image.AutoOrient();
            return image;
        }
        catch (MagickException e)
        {
            throw new FWError("打不開這張圖片：" + Fmt.Trunc(e.Message, 80));
        }
    }

    public static int? LossyQuality(string fmt) => fmt switch
    {
        "jpg" => 90,
        "heic" => 82,
        "webp" => 84,
        "avif" => 62,
        _ => null,
    };

    /// <summary>The format to write when a tool keeps "the same" format as the source.</summary>
    public static string SameFormat(string path)
    {
        string ext = Catalog.CanonicalExt(Kinds.Ext(path));
        return ext is "jpg" or "png" or "heic" or "tiff" or "gif" or "bmp" or "webp" or "avif" ? ext : "png";
    }

    public static bool HasAlpha(IMagickImage<byte> image) => image.HasAlpha && !image.IsOpaque;

    public static async Task Write(IMagickImage<byte> image, string output, string fmt, int? quality = null)
    {
        if (fmt == "pdf")
        {
            PdfOps.ImagesToPdf(new[] { image }, output);
            return;
        }
        if (!Writers.TryGetValue(fmt, out var format)) throw new FWError("不支援寫入 " + fmt.ToUpperInvariant());
        using var copy = image.Clone();
        // JPEG, BMP and AVIF here have no transparency: flatten onto white first.
        if ((fmt is "jpg" or "bmp" or "avif") && copy.HasAlpha)
        {
            copy.BackgroundColor = MagickColors.White;
            copy.Alpha(AlphaOption.Remove);
        }
        if (quality is int q) copy.Quality = q;
        if (fmt == "tiff") copy.Settings.Compression = CompressionMethod.LZW;
        copy.Format = format;
        try
        {
            copy.Write(output);
        }
        catch (MagickException) when (fmt == "avif" && Shell.Ffmpeg != null)
        {
            Out.TryDelete(output);
            await WriteAvifWithFfmpeg(copy, output, quality ?? 62);
        }
        catch (MagickException e)
        {
            Out.TryDelete(output);
            throw new FWError("寫入 " + fmt.ToUpperInvariant() + " 失敗：" + Fmt.Trunc(e.Message, 80));
        }
    }

    private static async Task WriteAvifWithFfmpeg(IMagickImage<byte> image, string output, int quality)
    {
        string tmp = Out.TempDir();
        try
        {
            string input = Path.Combine(tmp, "in.png");
            image.Write(input, MagickFormat.Png);
            int crf = (int)Math.Round(52 - quality / 100.0 * 34);
            await Shell.Run(Shell.Ffmpeg!, new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", input, "-frames:v", "1",
                "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p", "-c:v", "libsvtav1", "-crf", crf.ToString(), "-preset", "6", "-f", "avif", output,
            });
        }
        catch
        {
            Out.TryDelete(output);
            throw;
        }
        finally
        {
            Out.TryDelete(tmp);
        }
    }

    public static OpResult ShrinkResult(string source, string output)
    {
        long before = Fmt.Size(source), after = Fmt.Size(output);
        if (after >= before)
        {
            Out.TryDelete(output);
            throw new FWError("已經很小了，壓不動了");
        }
        int saved = (int)((1 - after / (double)Math.Max(before, 1)) * 100);
        return new OpResult(output) { Note = "體積 -" + saved + "%" };
    }

    private static void FitWithin(IMagickImage<byte> image, int longSide)
    {
        if (Math.Max(image.Width, image.Height) <= longSide) return;
        image.Resize(new MagickGeometry(longSide, longSide));
    }

    // MARK: - Actions

    public static async Task<OpResult> Convert(string path, string fmt)
    {
        using var image = Load(path);
        string output = Out.Target(path, null, fmt);
        await Write(image, output, fmt, LossyQuality(fmt));
        return new OpResult(output);
    }

    public static async Task<OpResult> Compress(string path)
    {
        using var image = Load(path);
        FitWithin(image, 4096);
        string source = SameFormat(path);
        string fmt;
        int? quality;
        switch (source)
        {
            case "jpg": fmt = "jpg"; quality = 70; break;
            case "heic": fmt = "heic"; quality = 55; break;
            case "webp": fmt = "webp"; quality = 70; break;
            case "avif": fmt = "avif"; quality = 45; break;
            default:
                if (!HasAlpha(image))
                {
                    fmt = "jpg";
                    quality = 78;
                }
                else
                {
                    fmt = "webp";
                    quality = 78;
                }
                break;
        }
        string output = Out.Target(path, "壓縮", fmt);
        await Write(image, output, fmt, quality);
        return ShrinkResult(path, output);
    }

    public static async Task<OpResult> Half(string path)
    {
        using var image = Load(path);
        string fmt = SameFormat(path);
        string output = Out.Target(path, "縮小", fmt);
        image.Resize(new Percentage(50));
        await Write(image, output, fmt, LossyQuality(fmt));
        return new OpResult(output) { Note = image.Width + "×" + image.Height };
    }

    public static async Task<OpResult> Cutout(string path)
    {
        using var image = Load(path);
        using var masked = await AgentWheel.Cutout.Run(image);
        string output = Out.Target(path, "去背", "png");
        await Write(masked, output, "png");
        return new OpResult(output) { Note = "背景已去掉" };
    }

    public static async Task<OpResult> Ocr(string path)
    {
        using var image = Load(path);
        string text = (await AgentWheel.Ocr.Recognize(image)).Trim();
        if (text.Length == 0) throw new FWError("圖裡沒認出文字");
        string output = Out.Target(path, "文字", "txt");
        await File.WriteAllTextAsync(output, text);
        return new OpResult(output) { Note = "認出 " + Fmt.Count(text.Length) + " 字，已複製", Clip = text };
    }

    public static async Task<OpResult> Mono(string path)
    {
        using var image = Load(path);
        image.Grayscale();
        string fmt = SameFormat(path);
        string output = Out.Target(path, "黑白", fmt);
        await Write(image, output, fmt, LossyQuality(fmt));
        return new OpResult(output);
    }

    public static async Task<OpResult> Rotate(string path)
    {
        using var image = Load(path);
        image.Rotate(90);
        string fmt = SameFormat(path);
        string output = Out.Target(path, "旋轉", fmt);
        await Write(image, output, fmt, fmt == "jpg" ? 95 : LossyQuality(fmt));
        return new OpResult(output);
    }

    /// <summary>Removes EXIF/GPS/XMP. JPEG and PNG are rewritten without re-encoding the pixels.</summary>
    public static async Task<OpResult> Strip(string path)
    {
        string ext = Kinds.Ext(path);
        byte[] data = await File.ReadAllBytesAsync(path);
        byte[]? stripped = null;
        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8) stripped = StripJpeg(data, Orientation(path));
        else if (data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') stripped = StripPng(data);
        if (stripped != null)
        {
            string output = Out.Target(path, "去隱私", ext.Length == 0 ? "jpg" : ext);
            await File.WriteAllBytesAsync(output, stripped);
            return new OpResult(output) { Note = Privacy.EvadedNote };
        }
        using var image = Load(path);
        image.Strip();
        string fmt = SameFormat(path);
        string other = Out.Target(path, "去隱私", fmt);
        await Write(image, other, fmt, fmt == "jpg" ? 95 : LossyQuality(fmt));
        return new OpResult(other) { Note = Privacy.EvadedNote };
    }

    private static int Orientation(string path)
    {
        try
        {
            var ifd0 = ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out int o)) return o;
        }
        catch
        {
            // no readable EXIF
        }
        return 1;
    }

    /// <summary>Drops APP1 (EXIF/XMP), APP13 (IPTC), comments and vendor segments; keeps JFIF, ICC and Adobe.</summary>
    private static byte[]? StripJpeg(byte[] data, int orientation)
    {
        using var output = new MemoryStream();
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        if (orientation is > 1 and <= 8)
        {
            // A minimal EXIF block that keeps only the orientation, so the photo still shows upright.
            byte[] exif =
            {
                (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
                0, 1,
                0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,
                0, 0, 0, 0,
            };
            output.WriteByte(0xFF);
            output.WriteByte(0xE1);
            int length = exif.Length + 2;
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)(length & 0xFF));
            output.Write(exif, 0, exif.Length);
        }
        int i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return null; // not a clean marker stream: let Magick handle it
            byte marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker == 0xDA || marker == 0xD9)
            {
                output.Write(data, i, data.Length - i);
                return output.ToArray();
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.Write(data, i, 2);
                i += 2;
                continue;
            }
            int len = (data[i + 2] << 8) | data[i + 3];
            if (i + 2 + len > data.Length) return null;
            bool personal = marker == 0xE1 || marker == 0xED || marker == 0xFE || (marker >= 0xE3 && marker <= 0xEF && marker != 0xEE);
            if (!personal) output.Write(data, i, 2 + len);
            i += 2 + len;
        }
        return null;
    }

    /// <summary>Drops text, EXIF and time chunks from a PNG; pixel data is untouched.</summary>
    private static byte[]? StripPng(byte[] data)
    {
        var drop = new HashSet<string> { "eXIf", "tEXt", "zTXt", "iTXt", "tIME" };
        using var output = new MemoryStream();
        output.Write(data, 0, 8);
        int i = 8;
        while (i + 12 <= data.Length)
        {
            int len = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
            if (len < 0 || i + 12 + len > data.Length) return null;
            string type = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            if (!drop.Contains(type)) output.Write(data, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") return output.ToArray();
        }
        return null;
    }

    /// <summary>Gradient backdrop with padding, rounded corners and a soft shadow.</summary>
    public static async Task<OpResult> Backdrop(string path)
    {
        using var image = Load(path);
        FitWithin(image, 4000);
        byte[] png = image.ToByteArray(MagickFormat.Png);
        int w = image.Width, h = image.Height;
        byte[] framed = await Sta.Run(() => Render.Backdrop(png, w, h));
        string output = Out.Target(path, "加背景", "png");
        await File.WriteAllBytesAsync(output, framed);
        return new OpResult(output);
    }
}
