using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImageMagick;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AgentWheel;

/// <summary>Text recognition with the OCR engine built into Windows 10 / 11.</summary>
public static class Ocr
{
    private static OcrEngine? Engine()
    {
        foreach (var tag in new[] { "zh-Hant-TW", "zh-TW", "zh-Hant", "zh-Hans-CN", "zh-CN" })
        {
            try
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine != null) return engine;
                }
            }
            catch
            {
                // unknown tag on this system
            }
        }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public static async Task<string> Recognize(IMagickImage<byte> image)
    {
        var engine = Engine() ?? throw new FWError("Windows 沒有可用的文字辨識語言：到「設定 → 時間與語言 → 語言」加入中文");
        using var copy = image.Clone();
        int max = (int)OcrEngine.MaxImageDimension;
        if (Math.Max(copy.Width, copy.Height) > max) copy.Resize(new MagickGeometry(max, max));
        byte[] png = copy.ToByteArray(MagickFormat.Png);
        using var stream = new MemoryStream(png).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(line => Tidy(line.Text)));
    }

    /// <summary>Windows OCR puts a space between every CJK character; take those back out.</summary>
    private static string Tidy(string text)
    {
        const string cjk = @"[⺀-⿟　-〿㐀-䶿一-鿿豈-﫿＀-￯]";
        return Regex.Replace(text, "(?<=" + cjk + @")\s+(?=" + cjk + ")", "");
    }
}
