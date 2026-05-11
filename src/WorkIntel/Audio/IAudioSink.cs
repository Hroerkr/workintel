using System.Threading;
using System.Threading.Tasks;

namespace WorkIntel.Audio;

/// <summary>
/// Downstream consumer of speech segments. The capture pipeline emits one of these
/// per VAD-bounded utterance. The next phase will replace <see cref="LoggingAudioSink"/>
/// with a Whisper-backed implementation that runs transcription, then hands the
/// transcript to the intent extractor and integration dispatcher.
/// </summary>
public interface IAudioSink
{
    /// <summary>
    /// Process a finalized speech segment. Must not throw — the audio pipeline does
    /// not have a sensible way to recover, so implementations should log and swallow.
    /// </summary>
    ValueTask PushAsync(SpeechSegment segment, CancellationToken ct);
}
