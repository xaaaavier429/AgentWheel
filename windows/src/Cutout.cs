using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AgentWheel;

/// <summary>
/// Background removal with the open U²-Net (u2netp, Apache-2.0) segmentation model.
/// The release zip ships the model in models\; otherwise it is downloaded once on first use.
/// </summary>
public static class Cutout
{
    private const string ModelName = "u2netp.onnx";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    private const int Side = 320;
    private static InferenceSession? session;
    private static readonly object Gate = new();

    private static string LocalModel => Path.Combine(Log.Folder, "models", ModelName);

    private static async Task<string> EnsureModel()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "models", ModelName);
        if (File.Exists(bundled)) return bundled;
        if (File.Exists(LocalModel)) return LocalModel;
        Directory.CreateDirectory(Path.GetDirectoryName(LocalModel)!);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            byte[] bytes = await http.GetByteArrayAsync(ModelUrl);
            string tmp = LocalModel + ".part";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, LocalModel, true);
            return LocalModel;
        }
        catch (Exception e)
        {
            throw new FWError("第一次去背需要下載模型（約 5 MB），下載失敗：" + Fmt.Trunc(e.Message, 60));
        }
    }

    public static async Task<IMagickImage<byte>> Run(IMagickImage<byte> image)
    {
        string modelPath = await EnsureModel();
        InferenceSession s;
        lock (Gate) s = session ??= new InferenceSession(modelPath);

        // Input: 320×320 RGB, scaled by the image maximum, then ImageNet mean / std.
        using var small = image.Clone();
        small.Alpha(AlphaOption.Off);
        small.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        byte[] rgb;
        using (var pixels = small.GetPixels())
            rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new FWError("讀不出像素");
        float peak = Math.Max(1, (float)rgb.Max());
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };
        var tensor = new DenseTensor<float>(new[] { 1, 3, Side, Side });
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                int idx = (y * Side + x) * 3;
                for (int c = 0; c < 3; c++) tensor[0, c, y, x] = (rgb[idx + c] / peak - mean[c]) / std[c];
            }
        }

        string inputName = s.InputMetadata.Keys.First();
        using var results = s.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();
        float lo = float.MaxValue, hi = float.MinValue;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                float v = output[0, 0, y, x];
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        if (hi - lo < 1e-6 || hi < 0.1f) throw new FWError("沒找到可以去背的主體");

        // The mask, scaled back up to the photo, becomes its alpha channel.
        var maskBytes = new byte[Side * Side * 3];
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                byte v = (byte)Math.Round(Math.Clamp((output[0, 0, y, x] - lo) / (hi - lo), 0, 1) * 255);
                int idx = (y * Side + x) * 3;
                maskBytes[idx] = v;
                maskBytes[idx + 1] = v;
                maskBytes[idx + 2] = v;
            }
        using var mask = new MagickImage();
        mask.ReadPixels(maskBytes, new PixelReadSettings(Side, Side, StorageType.Char, PixelMapping.RGB));
        mask.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
        var result = image.Clone();
        result.Alpha(AlphaOption.Set);
        result.Composite(mask, CompositeOperator.CopyAlpha);
        return result;
    }
}
