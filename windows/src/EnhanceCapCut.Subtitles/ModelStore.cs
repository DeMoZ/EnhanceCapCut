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

    /// <summary>
    /// Chatterbox Multilingual V3 (Resemble AI, MIT) as ONNX, with the 4-bit language model: voice cloning
    /// (<see cref="VoiceCloner"/>). Pinned to a revision of the export so the files never change under us. The same
    /// files and folder as the macOS version.
    /// </summary>
    public static readonly IReadOnlyList<ModelFile> VoiceCloning = new (string Name, long Bytes)[]
    {
        ("tokenizer.json", 72_765),
        ("onnx/speech_encoder.onnx", 1_204_600), ("onnx/speech_encoder.onnx_data", 591_274_880),
        ("onnx/embed_tokens.onnx", 17_616), ("onnx/embed_tokens.onnx_data", 68_808_704),
        ("onnx/language_model_q4.onnx", 228_633), ("onnx/language_model_q4.onnx_data", 353_621_248),
        ("onnx/conditional_decoder.onnx", 6_371_670), ("onnx/conditional_decoder.onnx_data", 533_970_816),
    }.Select(f => new ModelFile("chatterbox-v3", f.Name,
        "https://huggingface.co/Masterx/chatterbox-multilingual-v3-ONNX/resolve/19b94afe41de738624b2f3afc75b4cd56221fbeb/" + f.Name,
        f.Bytes)).ToList();

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

    /// <summary>The folder a set of files lives in.</summary>
    public static string FolderOf(IReadOnlyList<ModelFile> files) => Path.Combine(Root, files.Count > 0 ? files[0].Folder : "");

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
