using System;
using System.IO;
using WorkIntel.App;

namespace WorkIntel.Transcription;

/// <summary>Which ggml weight file to load. English-only models are noticeably faster
/// and smaller for English-only workloads.</summary>
public enum WhisperModel
{
    TinyEn,
    BaseEn,
    SmallEn,
    MediumEn,
    LargeV3
}

/// <summary>
/// Configuration for the Whisper.net transcriber. Defaults are tuned for the scaffold:
/// base.en gives a reasonable size/quality tradeoff (~140 MB, ~1× realtime on modern CPUs).
/// </summary>
public sealed class WhisperOptions
{
    /// <summary>Where ggml model files are cached. Defaults to <c>%LOCALAPPDATA%\WorkIntel\models</c>.</summary>
    public string ModelDirectory { get; set; } = Path.Combine(Log.LogDirectory, "models");

    public WhisperModel Model { get; set; } = WhisperModel.BaseEn;

    /// <summary>BCP-47 language code, or "auto" to detect.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Optional bias prompt fed to Whisper before each segment (≤224 tokens). Useful
    /// to lock in domain vocabulary like project / customer names. Null = no prompt.
    /// </summary>
    public string? InitialPrompt { get; set; } = null;

    /// <summary>Worker thread count for the native runtime. Half of logical cores is a safe default.</summary>
    public int Threads { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Translate non-English speech to English instead of transcribing in-language.</summary>
    public bool Translate { get; set; } = false;

    /// <summary>Skip segments shorter than this from being submitted to Whisper at all.</summary>
    public TimeSpan MinSegmentToTranscribe { get; set; } = TimeSpan.FromMilliseconds(400);
}
