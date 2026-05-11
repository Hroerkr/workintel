using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Pipeline;

namespace WorkIntel.Transcription;

/// <summary>
/// <see cref="ITranscriber"/> backed by Whisper.net + the bundled native runtime.
/// Lifecycle (lazy init, serialized inference, drain-on-dispose) is inherited
/// from <see cref="LazyNativeModel"/>; this class only owns the Whisper-specific
/// configuration, factory/processor handles, and inference body.
/// </summary>
public sealed class WhisperTranscriber : LazyNativeModel, ITranscriber
{
    private readonly WhisperOptions _options;
    private readonly WhisperModelStore _modelStore;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperTranscriber(WhisperOptions options, WhisperModelStore modelStore)
    {
        _options = options;
        _modelStore = modelStore;
    }

    public async Task<TranscriptionResult> TranscribeAsync(SpeechSegment segment, CancellationToken ct)
    {
        if (segment.SampleRate != 16_000 || segment.Channels != 1)
        {
            throw new InvalidOperationException(
                $"Whisper expects 16 kHz mono PCM; got {segment.SampleRate} Hz / {segment.Channels} ch");
        }

        if (segment.Duration < _options.MinSegmentToTranscribe)
            return new TranscriptionResult(string.Empty, _options.Language, 0d, segment);

        if (!await EnsureInitializedAsync(ct).ConfigureAwait(false))
            return new TranscriptionResult(string.Empty, _options.Language, 0d, segment);

        return await RunSerializedAsync(innerCt => InferAsync(segment, innerCt), ct).ConfigureAwait(false);
    }

    private async Task<TranscriptionResult> InferAsync(SpeechSegment segment, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        string detectedLang = _options.Language;
        int parts = 0;

        await foreach (var s in _processor!.ProcessAsync(segment.Samples.AsMemory(), ct).ConfigureAwait(false))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s.Text.Trim());
            if (!string.IsNullOrWhiteSpace(s.Language)) detectedLang = s.Language;
            parts++;
        }

        string text = sb.ToString().Trim();
        sw.Stop();
        double rtf = sw.Elapsed.TotalSeconds / segment.Duration.TotalSeconds;
        Log.Info($"transcribed {segment.Duration.TotalSeconds:F1}s in {sw.Elapsed.TotalSeconds:F1}s " +
                 $"(RTF={rtf:F2}, parts={parts}, lang={detectedLang}): \"{Truncate(text, 120)}\"");

        // Whisper.net doesn't surface a single utterance-level confidence; pinned at 1.0 for now.
        return new TranscriptionResult(text, detectedLang, Confidence: 1d, segment);
    }

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<DownloadProgress>(p =>
            Log.Info($"whisper model download: {p}"));
        string modelPath = await _modelStore.EnsureAsync(_options.Model, progress, ct).ConfigureAwait(false);

        Log.Info($"loading whisper model: {modelPath}");
        _factory = WhisperFactory.FromPath(modelPath);

        var builder = _factory.CreateBuilder().WithThreads(_options.Threads);

        if (string.Equals(_options.Language, "auto", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithLanguageDetection();
        else
            builder = builder.WithLanguage(_options.Language);

        if (_options.Translate)
            builder = builder.WithTranslate();

        if (!string.IsNullOrWhiteSpace(_options.InitialPrompt))
            builder = builder.WithPrompt(_options.InitialPrompt);

        _processor = builder.Build();
        Log.Info("whisper processor ready");
    }

    protected override async ValueTask ReleaseResourcesAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }
        _factory?.Dispose();
        _factory = null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
