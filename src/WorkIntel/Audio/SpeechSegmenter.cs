using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Audio;

/// <summary>
/// Bridges per-frame VAD decisions into bounded <see cref="SpeechSegment"/> events.
/// Accepts a stream of mono 16 kHz float samples (any length), buffers them into
/// fixed-size analysis frames, and emits a complete segment to the configured
/// sink when speech ends.
/// </summary>
public sealed class SpeechSegmenter
{
    public const int TargetSampleRate = 16_000;
    public const int FrameSamples = 320; // 20 ms @ 16 kHz

    private readonly EnergyVad _vad;
    private readonly IAudioSink _sink;
    private readonly StateManager _state;

    // Frame-accumulator: incoming chunks are arbitrary sizes; we batch into 320-sample frames.
    private readonly float[] _frameBuffer = new float[FrameSamples];
    private int _frameFill;

    // Active-segment buffer: appended-to while VAD is in speech.
    private readonly List<float> _segmentSamples = new(capacity: TargetSampleRate * 5);
    private DateTimeOffset _segmentStartedAt;
    private float _segmentPeak;
    private double _segmentSumSq;
    private int _segmentSampleCount;

    public event EventHandler<float>? LevelUpdated; // dBFS, for optional UI metering

    public SpeechSegmenter(EnergyVad vad, IAudioSink sink, StateManager state)
    {
        _vad = vad;
        _sink = sink;
        _state = state;
    }

    /// <summary>Feed mono 16 kHz float samples. Safe to call from the audio thread; segment
    /// finalization is dispatched onto the thread pool so the audio callback never blocks.</summary>
    public void Push(ReadOnlySpan<float> samples, DateTimeOffset now)
    {
        int offset = 0;
        while (offset < samples.Length)
        {
            int copy = Math.Min(FrameSamples - _frameFill, samples.Length - offset);
            samples.Slice(offset, copy).CopyTo(_frameBuffer.AsSpan(_frameFill, copy));
            _frameFill += copy;
            offset += copy;

            if (_frameFill == FrameSamples)
            {
                ProcessFrame(now);
                _frameFill = 0;
            }
        }
    }

    private void ProcessFrame(DateTimeOffset frameTime)
    {
        VadDecision decision = _vad.Process(_frameBuffer, frameTime);
        LevelUpdated?.Invoke(this, decision.FrameDbfs);

        switch (decision.Event)
        {
            case VadEvent.SpeechStarted:
                ResetSegmentAccumulator(decision.SegmentStartedAt);
                AppendFrameToSegment();
                _state.NotifySpeechActivity();
                break;

            case VadEvent.SpeechContinuing:
                AppendFrameToSegment();
                _state.NotifySpeechActivity();
                break;

            case VadEvent.SpeechEnded:
                AppendFrameToSegment();
                FinalizeAndEmit(frameTime);
                break;

            case VadEvent.None:
            default:
                // Silent frame, ignore.
                break;
        }
    }

    private void ResetSegmentAccumulator(DateTimeOffset startedAt)
    {
        _segmentSamples.Clear();
        _segmentStartedAt = startedAt;
        _segmentPeak = 0f;
        _segmentSumSq = 0;
        _segmentSampleCount = 0;
    }

    private void AppendFrameToSegment()
    {
        for (int i = 0; i < FrameSamples; i++)
        {
            float s = _frameBuffer[i];
            _segmentSamples.Add(s);
            float abs = Math.Abs(s);
            if (abs > _segmentPeak) _segmentPeak = abs;
            _segmentSumSq += s * s;
        }
        _segmentSampleCount += FrameSamples;
    }

    private void FinalizeAndEmit(DateTimeOffset endedAt)
    {
        if (_segmentSampleCount == 0) return;

        TimeSpan duration = TimeSpan.FromSeconds((double)_segmentSampleCount / TargetSampleRate);

        if (duration < _vad.MinSegmentDuration)
        {
            Log.Debug($"discarding sub-min segment: {duration.TotalMilliseconds:F0} ms");
            ResetSegmentAccumulator(default);
            return;
        }

        float rms = _segmentSampleCount > 0 ? (float)Math.Sqrt(_segmentSumSq / _segmentSampleCount) : 0f;
        var segment = new SpeechSegment(
            StartedAt: _segmentStartedAt,
            Duration: duration,
            SampleRate: TargetSampleRate,
            Channels: 1,
            Samples: _segmentSamples.ToArray(),
            PeakLevel: _segmentPeak,
            RmsLevel: rms);

        ResetSegmentAccumulator(default);

        // Hand off to sink on the thread pool — never block the audio path.
        _ = Task.Run(async () =>
        {
            try
            {
                await _sink.PushAsync(segment, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("audio sink threw", ex);
            }
        });
    }

    /// <summary>Drop any in-flight state. Call when capture is paused or stopped.</summary>
    public void Reset()
    {
        _vad.Reset();
        _frameFill = 0;
        ResetSegmentAccumulator(default);
    }
}
