using System.Net.Http;

namespace EnhanceCapCut.Subtitles;

/// <summary>A model file downloaded on first use.</summary>
public sealed record ModelFile(string Folder, string Name, string Url, long ApproximateBytes);

/// <summary>
/// The offline models subtitles need. They're too big to ship (and NLLB's licence is non-commercial), so they're
/// downloaded once from Hugging Face into <see cref="Root"/>: Whisper large-v3-turbo (whisper.cpp, 5-bit) for speech,
/// NLLB-200 distilled 600M (ONNX, 8-bit) for translation.
/// </summary>
public static class ModelStore
{
    const string Nllb = "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/main/";

    public static readonly ModelFile Whisper = new("whisper", "ggml-large-v3-turbo-q5_0.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin", 574_041_195);

    public static readonly IReadOnlyList<ModelFile> Translation =
    [
        new("nllb", "encoder_model_quantized.onnx", Nllb + "onnx/encoder_model_quantized.onnx", 419_120_483),
        new("nllb", "decoder_model_quantized.onnx", Nllb + "onnx/decoder_model_quantized.onnx", 470_533_055),
        new("nllb", "sentencepiece.bpe.model", Nllb + "sentencepiece.bpe.model", 4_852_054),
        new("nllb", "tokenizer.json", Nllb + "tokenizer.json", 17_331_224),
    ];

    /// <summary>
    /// <c>%LOCALAPPDATA%\EnhanceCapCut\Models</c> on Windows (<c>~/Library/Application Support/…</c> elsewhere);
    /// the <c>ECC_MODELS</c> environment variable overrides it.
    /// </summary>
    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ECC_MODELS") is { Length: > 0 } custom) return custom;
            var data = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            return Path.Combine(data, "EnhanceCapCut", "Models");
        }
    }

    public static string PathOf(ModelFile file) => Path.Combine(Root, file.Folder, file.Name);

    public static bool IsDownloaded(IEnumerable<ModelFile> files) => files.All(f => File.Exists(PathOf(f)));

    /// <summary>Downloads the missing <paramref name="files"/>; <paramref name="progress"/> gets 0…1 over all of them.</summary>
    public static async Task EnsureAsync(IReadOnlyList<ModelFile> files, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var missing = files.Where(f => !File.Exists(PathOf(f))).ToList();
        if (missing.Count == 0) return;
        var total = (double)missing.Sum(f => f.ApproximateBytes);
        long done = 0;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var file in missing)
        {
            var path = PathOf(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            using (var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(part);
                var buffer = new byte[1 << 20];
                int n;
                while ((n = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                    done += n;
                    progress?.Invoke(Math.Min(1, done / total));
                }
            }
            File.Move(part, path, overwrite: true);
        }
        progress?.Invoke(1);
    }
}
