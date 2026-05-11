using System;

namespace WorkIntel.Audio;

/// <summary>
/// A contiguous span of detected speech, normalized to mono 16 kHz float PCM.
/// This is the format Whisper.net consumes directly.
/// </summary>
public sealed record SpeechSegment(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    float[] Samples,
    float PeakLevel,
    float RmsLevel)
{
    public DateTimeOffset EndedAt => StartedAt + Duration;

    public override string ToString() =>
        $"SpeechSegment(start={StartedAt:HH:mm:ss.fff}, dur={Duration.TotalSeconds:F2}s, " +
        $"samples={Samples.Length}, peak={PeakLevel:F3}, rms={RmsLevel:F3})";
}
