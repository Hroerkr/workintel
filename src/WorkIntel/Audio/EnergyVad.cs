using System;

namespace WorkIntel.Audio;

/// <summary>
/// Lightweight energy-based voice activity detector. Operates on 16 kHz mono float PCM
/// in fixed-size frames (default 20 ms / 320 samples). Two-threshold hysteresis prevents
/// flapping; a hangover window keeps the segment open across short pauses.
/// </summary>
/// <remarks>
/// This is intentionally simple and good enough for scaffold + dogfooding. For production,
/// swap to Silero VAD (ONNX) or WebRTC VAD; the public surface (<see cref="Process"/> +
/// <see cref="VadDecision"/>) is shaped to make that swap mechanical.
/// </remarks>
public sealed class EnergyVad
{
    /// <summary>RMS dBFS above which silence transitions to speech.</summary>
    public float ActivationDb { get; set; } = -42f;

    /// <summary>RMS dBFS below which speech transitions back to silence (lower than activation = hysteresis).</summary>
    public float DeactivationDb { get; set; } = -50f;

    /// <summary>How long after the last loud frame we keep treating audio as speech.</summary>
    public TimeSpan Hangover { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Segments shorter than this on emit are discarded as noise.</summary>
    public TimeSpan MinSegmentDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Force-close a segment if it exceeds this length, even if speech continues.</summary>
    public TimeSpan MaxSegmentDuration { get; set; } = TimeSpan.FromSeconds(30);

    private bool _inSpeech;
    private DateTimeOffset _lastLoudFrameAt;
    private DateTimeOffset _segmentStartedAt;

    public bool InSpeech => _inSpeech;

    /// <summary>
    /// Feed one frame to the VAD and get the resulting transition (if any).
    /// </summary>
    public VadDecision Process(ReadOnlySpan<float> frame, DateTimeOffset frameTime)
    {
        float rms = ComputeRms(frame);
        float dbfs = RmsToDbfs(rms);

        if (!_inSpeech)
        {
            if (dbfs >= ActivationDb)
            {
                _inSpeech = true;
                _lastLoudFrameAt = frameTime;
                _segmentStartedAt = frameTime;
                return new VadDecision(VadEvent.SpeechStarted, _segmentStartedAt, dbfs, rms);
            }
            return new VadDecision(VadEvent.None, _segmentStartedAt, dbfs, rms);
        }

        // _inSpeech == true
        if (dbfs >= DeactivationDb)
        {
            _lastLoudFrameAt = frameTime;
        }

        TimeSpan segmentLength = frameTime - _segmentStartedAt;
        TimeSpan sinceLastLoud = frameTime - _lastLoudFrameAt;

        bool hangoverElapsed = sinceLastLoud >= Hangover;
        bool hitMaxLength = segmentLength >= MaxSegmentDuration;

        if (hangoverElapsed || hitMaxLength)
        {
            _inSpeech = false;
            return new VadDecision(VadEvent.SpeechEnded, _segmentStartedAt, dbfs, rms);
        }

        return new VadDecision(VadEvent.SpeechContinuing, _segmentStartedAt, dbfs, rms);
    }

    /// <summary>Manual reset (e.g. on pause).</summary>
    public void Reset()
    {
        _inSpeech = false;
        _lastLoudFrameAt = default;
        _segmentStartedAt = default;
    }

    private static float ComputeRms(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty) return 0f;
        double sumSq = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            float s = frame[i];
            sumSq += s * s;
        }
        return (float)Math.Sqrt(sumSq / frame.Length);
    }

    private static float RmsToDbfs(float rms)
    {
        // -inf dB floor at -90; avoids log(0).
        if (rms < 1e-9f) return -90f;
        return 20f * MathF.Log10(rms);
    }
}

public enum VadEvent
{
    None,
    SpeechStarted,
    SpeechContinuing,
    SpeechEnded
}

public readonly record struct VadDecision(
    VadEvent Event,
    DateTimeOffset SegmentStartedAt,
    float FrameDbfs,
    float FrameRms);
