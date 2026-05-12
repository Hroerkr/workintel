using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;
using WorkIntel.App;
using WorkIntel.Pipeline;
using WorkIntel.Tasks;
using WorkIntel.Transcription; // DownloadProgress

namespace WorkIntel.Intent;

/// <summary>
/// <see cref="IIntentExtractor"/> backed by LLamaSharp + a local gguf model
/// (Phi-3.5 Mini Instruct by default). Returns task candidates extracted from
/// the supplied transcript.
/// </summary>
public sealed class LocalIntentExtractor : LazyNativeModel, IIntentExtractor
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly LocalLlmOptions _options;
    private readonly LocalLlmModelStore _store;

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;

    public LocalIntentExtractor(LocalLlmOptions options, LocalLlmModelStore store)
    {
        _options = options;
        _store = store;
    }

    public async Task<IReadOnlyList<TaskCandidate>> ExtractAsync(TranscriptionResult transcription, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcription.Text)) return Array.Empty<TaskCandidate>();
        if (transcription.Text.Length < _options.MinTranscriptChars) return Array.Empty<TaskCandidate>();

        if (!await EnsureInitializedAsync(ct).ConfigureAwait(false))
            return Array.Empty<TaskCandidate>();

        return await RunSerializedAsync(innerCt => InferAsync(transcription, innerCt), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TaskCandidate>> InferAsync(TranscriptionResult transcription, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var executor = new StatelessExecutor(_weights!, _modelParams!);
        string prompt = BuildPhi35Prompt(transcription.Text);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxOutputTokens,
            AntiPrompts = new List<string> { "<|end|>", "<|endoftext|>", "<|user|>" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = _options.Temperature },
        };

        var sb = new StringBuilder(capacity: 1024);
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
        {
            sb.Append(token);
            if (sb.Length > 8 * 1024) break; // safety valve against runaway output
        }

        var tasks = ParseTasks(sb.ToString());
        sw.Stop();

        Log.Info($"task extraction: {tasks.Count} in {sw.Elapsed.TotalSeconds:F2}s for transcript of {transcription.Text.Length} chars");
        foreach (var t in tasks)
            Log.Debug($"  → \"{Truncate(t.Title, 80)}\" (conf {t.Confidence:F2})");

        return tasks;
    }

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<DownloadProgress>(p =>
            Log.Info($"local LLM model download: {p}"));
        string modelPath = await _store.EnsureAsync(_options.Model, progress, ct).ConfigureAwait(false);

        Log.Info($"loading local LLM: {modelPath}");
        _modelParams = new ModelParams(modelPath)
        {
            ContextSize = _options.ContextSize,
            GpuLayerCount = _options.GpuLayerCount,
            Threads = _options.Threads,
        };

        _weights = await LLamaWeights.LoadFromFileAsync(_modelParams, ct).ConfigureAwait(false);
        Log.Info($"local LLM loaded ({_options.Model}, ctx={_options.ContextSize}, threads={_options.Threads}, gpu_layers={_options.GpuLayerCount})");
    }

    protected override ValueTask ReleaseResourcesAsync()
    {
        _weights?.Dispose();
        _weights = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Phi-3.5 instruct chat template — must match the format the model was
    /// fine-tuned on or output is garbage.</summary>
    private static string BuildPhi35Prompt(string content) =>
        $"<|system|>\n{IntentSchema.SystemPrompt}<|end|>\n" +
        $"<|user|>\nInput: {content}\nOutput:<|end|>\n" +
        $"<|assistant|>\n";

    /// <summary>
    /// Robust JSON-array extraction. Phi-3.5 sometimes wraps output in code
    /// fences or trails extra commentary; we find the first top-level <c>[</c>,
    /// scan forward respecting bracket depth + string state, parse that slice.
    /// Internal so unit tests can drive it directly.
    /// </summary>
    internal static IReadOnlyList<TaskCandidate> ParseTasks(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<TaskCandidate>();

        int start = raw.IndexOf('[');
        if (start < 0) return Array.Empty<TaskCandidate>();

        int depth = 0;
        bool inString = false;
        bool escape = false;
        int end = -1;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }

        if (end < 0) return Array.Empty<TaskCandidate>();
        string json = raw.Substring(start, end - start + 1);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RawTask>>(json, JsonOpts);
            if (parsed is null) return Array.Empty<TaskCandidate>();

            var result = new List<TaskCandidate>(parsed.Count);
            foreach (var r in parsed)
            {
                if (string.IsNullOrWhiteSpace(r.Title)) continue;
                result.Add(new TaskCandidate(
                    Title: r.Title!.Trim(),
                    Description: NullIfBlank(r.Description),
                    Owner: NullIfBlank(r.Owner),
                    Deadline: NullIfBlank(r.Deadline),
                    Confidence: r.Confidence,
                    SourceQuote: r.SourceQuote ?? string.Empty));
            }
            return result;
        }
        catch (JsonException ex)
        {
            Log.Warn($"task JSON parse failed: {ex.Message}; raw=\"{Truncate(raw, 200)}\"");
            return Array.Empty<TaskCandidate>();
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private sealed class RawTask
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("owner")] public string? Owner { get; set; }
        [JsonPropertyName("deadline")] public string? Deadline { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("source_quote")] public string? SourceQuote { get; set; }
    }
}
