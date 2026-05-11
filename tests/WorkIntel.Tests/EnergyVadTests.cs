using System;
using WorkIntel.Audio;
using Xunit;

namespace WorkIntel.Tests;

public sealed class EnergyVadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>20 ms-equivalent buffer (320 samples) filled with a constant amplitude.
    /// Returns the buffer plus the dBFS the constant maps to.</summary>
    private static (float[] buffer, float dbfs) ConstantFrame(float amplitude)
    {
        var buf = new float[SpeechSegmenter.FrameSamples];
        Array.Fill(buf, amplitude);
        // RMS of a constant signal == |amplitude|, so dBFS = 20*log10(|a|).
        float dbfs = amplitude == 0f ? -90f : 20f * MathF.Log10(MathF.Abs(amplitude));
        return (buf, dbfs);
    }

    [Fact]
    public void SilentFrame_ProducesNoTransition_BeforeSpeech()
    {
        var vad = new EnergyVad();
        var (silent, _) = ConstantFrame(0f);

        var d = vad.Process(silent, T0);

        Assert.Equal(VadEvent.None, d.Event);
        Assert.False(vad.InSpeech);
    }

    [Fact]
    public void LoudFrame_OpensSegment()
    {
        var vad = new EnergyVad { ActivationDb = -20f };
        // 0.5 amplitude -> ~-6 dBFS, comfortably above -20 dBFS activation.
        var (loud, dbfs) = ConstantFrame(0.5f);
        Assert.True(dbfs >= -20f);

        var d = vad.Process(loud, T0);

        Assert.Equal(VadEvent.SpeechStarted, d.Event);
        Assert.True(vad.InSpeech);
    }

    [Fact]
    public void ContinuingLoudFrames_StayInSpeech()
    {
        var vad = new EnergyVad { ActivationDb = -20f, DeactivationDb = -30f };
        var (loud, _) = ConstantFrame(0.5f);

        vad.Process(loud, T0);
        var d2 = vad.Process(loud, T0.AddMilliseconds(20));
        var d3 = vad.Process(loud, T0.AddMilliseconds(40));

        Assert.Equal(VadEvent.SpeechContinuing, d2.Event);
        Assert.Equal(VadEvent.SpeechContinuing, d3.Event);
        Assert.True(vad.InSpeech);
    }

    [Fact]
    public void SilenceAfterHangover_ClosesSegment()
    {
        var vad = new EnergyVad
        {
            ActivationDb = -20f,
            DeactivationDb = -30f,
            Hangover = TimeSpan.FromMilliseconds(100),
        };
        var (loud, _) = ConstantFrame(0.5f);
        var (silent, _) = ConstantFrame(0f);

        vad.Process(loud, T0);                                 // SpeechStarted
        vad.Process(silent, T0.AddMilliseconds(50));           // still in hangover
        var inHang = vad.Process(silent, T0.AddMilliseconds(80));
        var ended  = vad.Process(silent, T0.AddMilliseconds(150)); // past hangover

        Assert.Equal(VadEvent.SpeechContinuing, inHang.Event);
        Assert.Equal(VadEvent.SpeechEnded, ended.Event);
        Assert.False(vad.InSpeech);
    }

    [Fact]
    public void MaxSegmentDuration_ForceClosesEvenWhileLoud()
    {
        var vad = new EnergyVad
        {
            ActivationDb = -20f,
            DeactivationDb = -30f,
            MaxSegmentDuration = TimeSpan.FromMilliseconds(100),
            Hangover = TimeSpan.FromSeconds(10), // doesn't matter, max wins
        };
        var (loud, _) = ConstantFrame(0.5f);

        vad.Process(loud, T0);
        var atCap = vad.Process(loud, T0.AddMilliseconds(120));

        Assert.Equal(VadEvent.SpeechEnded, atCap.Event);
        Assert.False(vad.InSpeech);
    }

    [Fact]
    public void Reset_ReturnsToBaseline()
    {
        var vad = new EnergyVad { ActivationDb = -20f };
        var (loud, _) = ConstantFrame(0.5f);

        vad.Process(loud, T0);
        Assert.True(vad.InSpeech);

        vad.Reset();

        Assert.False(vad.InSpeech);
        var (silent, _) = ConstantFrame(0f);
        var d = vad.Process(silent, T0.AddSeconds(1));
        Assert.Equal(VadEvent.None, d.Event);
    }

    [Theory]
    [InlineData(0f, -90f)]
    [InlineData(1.0f, 0f)]
    [InlineData(0.5f, -6.02f)]
    [InlineData(0.1f, -20f)]
    public void DbfsMapping_IsExpectedForConstantAmplitude(float amp, float expectedDbfs)
    {
        // Sanity-check the helper used by the rest of these tests.
        var (_, dbfs) = ConstantFrame(amp);
        Assert.InRange(dbfs, expectedDbfs - 0.1f, expectedDbfs + 0.1f);
    }
}
