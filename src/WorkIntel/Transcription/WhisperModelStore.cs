using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Transcription;

/// <summary>
/// Locates ggml model files on disk and downloads them on first use.
/// </summary>
/// <remarks>
/// The official whisper.cpp distributions live on Hugging Face under
/// <c>ggerganov/whisper.cpp</c>. We pull them via HTTPS and atomically rename
/// the temp file into place to avoid leaving a half-written file that loads
/// but fails inference.
/// </remarks>
public sealed class WhisperModelStore
{
    private static readonly IReadOnlyDictionary<WhisperModel, ModelDescriptor> Catalog =
        new Dictionary<WhisperModel, ModelDescriptor>
        {
            [WhisperModel.TinyEn]   = new("ggml-tiny.en.bin",   "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",   75 * 1024 * 1024),
            [WhisperModel.BaseEn]   = new("ggml-base.en.bin",   "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",  142 * 1024 * 1024),
            [WhisperModel.SmallEn]  = new("ggml-small.en.bin",  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin", 466 * 1024 * 1024),
            [WhisperModel.MediumEn] = new("ggml-medium.en.bin", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin",1500L * 1024 * 1024),
            [WhisperModel.LargeV3]  = new("ggml-large-v3.bin",  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin", 3100L * 1024 * 1024),
        };

    private readonly string _baseDir;
    private readonly SemaphoreSlim _downloadLock = new(1, 1); // serialize concurrent ensures of the same model

    public WhisperModelStore(string baseDir)
    {
        _baseDir = baseDir;
    }

    public string GetModelPath(WhisperModel model) =>
        Path.Combine(_baseDir, Catalog[model].FileName);

    public bool IsModelPresent(WhisperModel model)
    {
        var path = GetModelPath(model);
        if (!File.Exists(path)) return false;
        // Reject obviously truncated downloads. Real ggml files are tens-to-thousands of MB.
        var len = new FileInfo(path).Length;
        return len > 10 * 1024 * 1024;
    }

    /// <summary>
    /// Returns the absolute path to the model, downloading it first if necessary.
    /// </summary>
    public async Task<string> EnsureAsync(
        WhisperModel model,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var dest = GetModelPath(model);
        if (IsModelPresent(model)) return dest;

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock; another caller may have downloaded while we waited.
            if (IsModelPresent(model)) return dest;

            Directory.CreateDirectory(_baseDir);
            var descriptor = Catalog[model];
            var tmp = dest + ".downloading";
            if (File.Exists(tmp)) File.Delete(tmp);

            Log.Info($"downloading whisper model: {descriptor.FileName} from {descriptor.Url}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30) // medium/large can be slow on bad connections
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WorkIntel/0.1 (+https://github.com/anthropics)");

            using var resp = await http.GetAsync(descriptor.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? descriptor.ApproxBytes;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                var lastReport = DateTimeOffset.UtcNow;
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;

                    if ((DateTimeOffset.UtcNow - lastReport).TotalSeconds >= 2)
                    {
                        progress?.Report(new DownloadProgress(written, total));
                        lastReport = DateTimeOffset.UtcNow;
                    }
                }

                progress?.Report(new DownloadProgress(written, total));
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            Log.Info($"downloaded {descriptor.FileName} in {sw.Elapsed.TotalSeconds:F1}s ({new FileInfo(dest).Length / 1024 / 1024} MB)");
            return dest;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private sealed record ModelDescriptor(string FileName, string Url, long ApproxBytes);
}

public readonly record struct DownloadProgress(long BytesDownloaded, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes : 0d;
    public override string ToString() =>
        TotalBytes > 0
            ? $"{BytesDownloaded / 1024 / 1024} / {TotalBytes / 1024 / 1024} MB ({Fraction * 100:F1}%)"
            : $"{BytesDownloaded / 1024 / 1024} MB";
}
