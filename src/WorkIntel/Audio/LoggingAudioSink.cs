using System;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Audio;

/// <summary>
/// Placeholder sink for the scaffold phase: just logs each emitted segment.
/// Replace with a Whisper-backed sink that hands transcripts off to the
/// intent extractor and integration dispatcher.
/// </summary>
public sealed class LoggingAudioSink : IAudioSink
{
    public ValueTask PushAsync(SpeechSegment segment, CancellationToken ct)
    {
        Log.Info($"audio segment captured: {segment}");
        return ValueTask.CompletedTask;
    }
}
