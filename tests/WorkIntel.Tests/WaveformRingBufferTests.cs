using System;
using WorkIntel.Audio;
using Xunit;

namespace WorkIntel.Tests;

public sealed class WaveformRingBufferTests
{
    [Fact]
    public void Snapshot_BeforeAnyWrite_ReturnsZero()
    {
        var ring = new WaveformRingBuffer(8);
        var dest = new float[4];

        int got = ring.Snapshot(dest);

        Assert.Equal(0, got);
    }

    [Fact]
    public void WriteSmallerThanCapacity_SnapshotReturnsAllSamples()
    {
        var ring = new WaveformRingBuffer(8);
        ring.WriteSamples(new float[] { 1, 2, 3 });
        var dest = new float[8];

        int got = ring.Snapshot(dest);

        Assert.Equal(3, got);
        Assert.Equal(new float[] { 1, 2, 3 }, dest[..3]);
    }

    [Fact]
    public void WriteEqualsCapacity_SnapshotReturnsAll()
    {
        var ring = new WaveformRingBuffer(4);
        ring.WriteSamples(new float[] { 1, 2, 3, 4 });
        var dest = new float[4];

        int got = ring.Snapshot(dest);

        Assert.Equal(4, got);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, dest);
    }

    [Fact]
    public void WriteLargerThanCapacity_OnlyTailIsKept()
    {
        var ring = new WaveformRingBuffer(4);
        ring.WriteSamples(new float[] { 1, 2, 3, 4, 5, 6, 7 });
        var dest = new float[4];

        int got = ring.Snapshot(dest);

        Assert.Equal(4, got);
        Assert.Equal(new float[] { 4, 5, 6, 7 }, dest);
        Assert.Equal(7, ring.TotalSamplesWritten);
    }

    [Fact]
    public void MultipleWrites_WrapAround_PreserveOrder()
    {
        var ring = new WaveformRingBuffer(4);
        ring.WriteSamples(new float[] { 1, 2, 3 });
        ring.WriteSamples(new float[] { 4, 5 });   // forces wrap of 5
        ring.WriteSamples(new float[] { 6 });      // wraps further

        var dest = new float[4];
        int got = ring.Snapshot(dest);

        Assert.Equal(4, got);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, dest);
    }

    [Fact]
    public void Snapshot_RequestSmallerThanAvailable_ReturnsLatestN()
    {
        var ring = new WaveformRingBuffer(8);
        ring.WriteSamples(new float[] { 1, 2, 3, 4, 5, 6 });

        var dest = new float[3];
        int got = ring.Snapshot(dest);

        Assert.Equal(3, got);
        Assert.Equal(new float[] { 4, 5, 6 }, dest);
    }

    [Fact]
    public void OversizedWrite_AfterPriorSmallWrite_StillReturnsCorrectTail()
    {
        // Regression: pre-existing _writeIndex meant the big-chunk path used to land
        // data at the wrong physical offset. Snapshot must still return the latest
        // `cap` samples regardless of where the buffer was mid-cycle.
        var ring = new WaveformRingBuffer(4);
        ring.WriteSamples(new float[] { 1, 2, 3 });             // _writeIndex = 3
        ring.WriteSamples(new float[] { 4, 5, 6, 7, 8, 9, 10 }); // length 7 > cap 4

        var dest = new float[4];
        int got = ring.Snapshot(dest);

        Assert.Equal(4, got);
        Assert.Equal(new float[] { 7, 8, 9, 10 }, dest);
        Assert.Equal(10, ring.TotalSamplesWritten);
    }

    [Fact]
    public void EmptyWrite_IsNoOp()
    {
        var ring = new WaveformRingBuffer(4);
        ring.WriteSamples(Array.Empty<float>());

        Assert.Equal(0, ring.TotalSamplesWritten);
    }
}
