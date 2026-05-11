using System.Threading;
using System.Threading.Tasks;
using WorkIntel.Audio;

namespace WorkIntel.Pipeline;

/// <summary>
/// Phase 2 plug-point: turn a <see cref="SpeechSegment"/> into text.
/// Initial implementation will wrap Whisper.net (ggml model loaded once,
/// reused across segments). The interface is async so the implementation
/// can run inference on a background thread or batch.
/// </summary>
public interface ITranscriber
{
    Task<TranscriptionResult> TranscribeAsync(SpeechSegment segment, CancellationToken ct);
}

public sealed record TranscriptionResult(
    string Text,
    string Language,
    double Confidence,
    SpeechSegment Source);
