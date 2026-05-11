using System;
using System.IO;
using WorkIntel.App;

namespace WorkIntel.Intent;

/// <summary>
/// Which gguf weight file to load. Defaults to Phi-3.5 Mini Instruct at Q4_K_M
/// (~2.4 GB, ~6 GB resident, fast on a modern CPU).
/// </summary>
public enum LocalLlmModel
{
    /// <summary>Phi-3.5 Mini Instruct, Q4_K_M quantization. ~2.4 GB on disk.</summary>
    Phi35MiniInstructQ4,

    /// <summary>Phi-3.5 Mini Instruct, Q5_K_M quantization. ~2.8 GB on disk, slightly higher quality.</summary>
    Phi35MiniInstructQ5,

    /// <summary>Phi-3.5 Mini Instruct, Q8_0 quantization. ~4.0 GB on disk, near-lossless.</summary>
    Phi35MiniInstructQ8,
}

/// <summary>
/// Configuration for the local intent-extraction LLM (LLamaSharp + Phi-3.5 by default).
/// </summary>
public sealed class LocalLlmOptions
{
    /// <summary>Where gguf model files are cached. Shared with Whisper by default.</summary>
    public string ModelDirectory { get; set; } = Path.Combine(Log.LogDirectory, "models");

    public LocalLlmModel Model { get; set; } = LocalLlmModel.Phi35MiniInstructQ4;

    /// <summary>Context window in tokens. 4096 covers any plausible utterance + system prompt + JSON response.</summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Inference threads for the CPU backend. Half of logical cores is a safe default for a tray app.</summary>
    public int Threads { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>
    /// Number of model layers to offload to GPU. 0 = pure CPU. Phi-3.5 Mini has 33 layers;
    /// set to 33 for full GPU offload (only meaningful with a GPU backend package).
    /// </summary>
    public int GpuLayerCount { get; set; } = 0;

    /// <summary>Sampling temperature. Keep low — we want deterministic JSON output.</summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>Cap on generated tokens per call. Intents are tiny; 512 is generous.</summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>Skip transcripts shorter than this from being submitted at all.</summary>
    public int MinTranscriptChars { get; set; } = 4;
}
