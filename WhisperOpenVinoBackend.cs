using System.Net.Http;

namespace VoiceGuard;

/// <summary>
/// Optional Intel OpenVINO encoder model preparation.
/// The actual device selection is performed through Whisper.net's public API.
/// This helper only downloads and locates the OpenVINO IR model and cache folder.
/// </summary>
internal static class WhisperOpenVinoBackend
{
    private const string EncoderXmlName = "ggml-base.en-encoder-openvino.xml";
    private const string EncoderBinName = "ggml-base.en-encoder-openvino.bin";

    private const string EncoderXmlUrl =
        "https://huggingface.co/twdragon/whisper.cpp-openvino/resolve/main/base/ggml-base.en-encoder-openvino.xml";
    private const string EncoderBinUrl =
        "https://huggingface.co/twdragon/whisper.cpp-openvino/resolve/main/base/ggml-base.en-encoder-openvino.bin";

    public static bool IsIntelPlatform()
    {
        var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
        return identifier.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(string EncoderXmlPath, string CacheDirectory)?> PrepareNpuAsync(
        string modelDirectory,
        Action<string> log)
    {
        if (!IsIntelPlatform())
            return null;

        try
        {
            var xmlPath = Path.Combine(modelDirectory, EncoderXmlName);
            var binPath = Path.Combine(modelDirectory, EncoderBinName);

            Directory.CreateDirectory(modelDirectory);

            if (!File.Exists(xmlPath) || !File.Exists(binPath))
            {
                log("OPENVINO: downloading base.en encoder model...");

                if (!File.Exists(xmlPath))
                    await DownloadFileAsync(EncoderXmlUrl, xmlPath, log);

                if (!File.Exists(binPath))
                    await DownloadFileAsync(EncoderBinUrl, binPath, log);
            }

            if (!File.Exists(xmlPath) || !File.Exists(binPath))
            {
                log("OPENVINO: encoder model files are missing — using CPU fallback.");
                return null;
            }

            var cacheDirectory = Path.Combine(
                modelDirectory,
                "ggml-base.en-encoder-openvino-cache-NPU");
            Directory.CreateDirectory(cacheDirectory);

            return (xmlPath, cacheDirectory);
        }
        catch (Exception ex)
        {
            log("OPENVINO: encoder preparation failed — using CPU fallback.");
            log($"OPENVINO DETAIL: {ex.Message}");
            return null;
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string path,
        Action<string> log)
    {
        var tempPath = path + ".download";

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceGuard/6.6");

        await using (var input = await client.GetStreamAsync(url))
        await using (var output = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            useAsync: true))
        {
            await input.CopyToAsync(output);
        }

        File.Move(tempPath, path, true);
        log($"OPENVINO: downloaded {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024 / 1024} MB).");
    }
}
