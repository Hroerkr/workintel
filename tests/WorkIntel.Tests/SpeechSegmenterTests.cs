using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;
using WorkIntel.Audio;
using Xunit;

namespace WorkIntel.Tests;

public sealed class SpeechSegmenterTests
{
    private sealed class CapturingSink : IAudioSink
    {
        public List<SpeechSegment> Captured { get; } = new();
        public ManualResetEventSlim AnySegment { get; } = new(initialState: false);

        public ValueTask PushAsync(SpeechSegment segment, CancellationToken ct)
        {
            lock (Captured) Captured.Add(segment);
            AnySegment.Set();
            return ValueTask.CompletedTask;
        }
    }

    private static float[] Silence(int sampleCount)
    {
        return new float[sampleCount];
    }

    private static float[] Tone(int sampleCount, float amplitude)
    {
        var b = new float[sampleCount];
        Array.Fill(b, amplitude);
        return b;
    }

    [Fact]
    public void SilenceOnly_ProducesNoSegments()
    {
        var sink = new CapturingSink();
        var seg = new SpeechSegmenter(new EnergyVad(), sink, new StateManager());

        // 50 frames of pure silence
        for (int i = 0; i < 50; i++)
            seg.Push(Silence(SpeechSegmenter.FrameSamples), DateTimeOffset.UtcNow);

        Assert.Empty(sink.Captured);
    }

    [Fact]
    public void LoudThenSilent_EmitsOneSegment()
    {
        var sink = new CapturingSink();
        var vad = new EnergyVad
        {
            ActivationDb = -20f,
            DeactivationDb = -30f,
            Hangover = TimeSpan.FromMilliseconds(40),
            MinSegmentDuration = TimeSpan.FromMilliseconds(20),
        };
        using var state = new StateManager();
        var seg = new SpeechSegmenter(vad, sink, state);

        var t = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

        // ~200 ms of "speech" (loud constant)
        for (int i = 0; i < 10; i++)
        {
            seg.Push(Tone(SpeechSegmenter.FrameSamples, 0.5f), t);
            t = t.AddMilliseconds(20);
        }
        // ~200 ms of silence — past hangover
        for (int i = 0; i < 10; i++)
        {
            seg.Push(Silence(SpeechSegmenter.FrameSamples), t);
            t = t.AddMilliseconds(20);
        }

        // Segment finalization is dispatched onto the thread pool; wait for it.
        Assert.True(sink.AnySegment.Wait(TimeSpan.FromSeconds(2)),
            "expected at least one SpeechSegment emitted within 2 seconds");

        Assert.Single(sink.Captured);
        var emitted = sink.Captured[0];
        Assert.Equal(SpeechSegmenter.TargetSampleRate, emitted.SampleRate);
        Assert.Equal(1, emitted.Channels);
        Assert.True(emitted.Duration.TotalMilliseconds >= 100);
        Assert.True(emitted.PeakLevel > 0.4f);
    }

    [Fact]
    public void SubMinSegment_IsDiscarded()
    {
        var sink = new CapturingSink();
        var vad = new EnergyVad
        {
            ActivationDb = -20f,
            DeactivationDb = -30f,
            Hangover = TimeSpan.FromMilliseconds(40),
            MinSegmentDuration = TimeSpan.FromSeconds(1), // very high
        };
        var seg = new SpeechSegmenter(vad, sink, new StateManager());

        var t = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

        // Brief blip well under MinSegmentDuration
        for (int i = 0; i < 3; i++) { seg.Push(Tone(SpeechSegmenter.FrameSamples, 0.5f), t); t = t.AddMilliseconds(20); }
        for (int i = 0; i < 5; i++) { seg.Push(Silence(SpeechSegmenter.FrameSamples), t); t = t.AddMilliseconds(20); }

        // No segment passed to the sink (segmenter discards before async hop).
        Thread.Sleep(50);
        Assert.Empty(sink.Captured);
    }

    [Fact]
    public void Reset_ClearsInFlightSegment()
    {
        var sink = new CapturingSink();
        var vad = new EnergyVad { ActivationDb = -20f };
        var seg = new SpeechSegmenter(vad, sink, new StateManager());

        var t = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        seg.Push(Tone(SpeechSegmenter.FrameSamples, 0.5f), t);
        Assert.True(vad.InSpeech);

        seg.Reset();

        Assert.False(vad.InSpeech);
        Assert.Empty(sink.Captured);
    }
}
