using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;
using WorkIntel.Transcription; // re-use DownloadProgress

namespace WorkIntel.Intent;

/// <summary>
/// Locates Phi-3.5 (and friends) gguf model files on disk and downloads them on
/// first use. Mirrors <see cref="WhisperModelStore"/> in shape — atomic
/// <c>.downloading → final</c> rename so a killed download never leaves a corrupt
/// file the loader will accept.
/// </summary>
/// <remarks>
/// Default catalog points at the community-maintained Bartowski quants on
/// Hugging Face. The official Microsoft release of Phi-3.5 ships safetensors,
/// not gguf, so we go through the conversion the llama.cpp community already did.
/// Swap the URLs for an internal artifact mirror in air-gapped environments.
/// </remarks>
public sealed class LocalLlmModelStore
{
    private static readonly IReadOnlyDictionary<LocalLlmModel, ModelDescriptor> Catalog =
        new Dictionary<LocalLlmModel, ModelDescriptor>
        {
            [LocalLlmModel.Phi35MiniInstructQ4] = new(
                "Phi-3.5-mini-instruct-Q4_K_M.gguf",
                "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q4_K_M.gguf",
                ApproxBytes: 2_400L * 1024 * 1024),

            [LocalLlmModel.Phi35MiniInstructQ5] = new(
                "Phi-3.5-mini-instruct-Q5_K_M.gguf",
                "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q5_K_M.gguf",
                ApproxBytes: 2_820L * 1024 * 1024),

            [LocalLlmModel.Phi35MiniInstructQ8] = new(
                "Phi-3.5-mini-instruct-Q8_0.gguf",
                "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q8_0.gguf",
                ApproxBytes: 4_060L * 1024 * 1024),
        };

    private readonly string _baseDir;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public LocalLlmModelStore(string baseDir)
    {
        _baseDir = baseDir;
    }

    public string GetModelPath(LocalLlmModel model) =>
        Path.Combine(_baseDir, Catalog[model].FileName);

    public bool IsModelPresent(LocalLlmModel model)
    {
        var path = GetModelPath(model);
        if (!File.Exists(path)) return false;
        // Reject obviously truncated downloads.
        return new FileInfo(path).Length > 100L * 1024 * 1024;
    }

    public async Task<string> EnsureAsync(
        LocalLlmModel model,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var dest = GetModelPath(model);
        if (IsModelPresent(model)) return dest;

        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsModelPresent(model)) return dest;

            Directory.CreateDirectory(_baseDir);
            var descriptor = Catalog[model];
            var tmp = dest + ".downloading";
            if (File.Exists(tmp)) File.Delete(tmp);

            Log.Info($"downloading local LLM: {descriptor.FileName} from {descriptor.Url}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WorkIntel/0.1");

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

                    if ((DateTimeOffset.UtcNow - lastReport).TotalSeconds >= 5)
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
