using System;

namespace WorkIntel.Audio;

/// <summary>
/// Lock-protected single-writer / multiple-reader ring buffer of float samples.
/// The audio thread writes; the UI thread snapshots the most-recent N samples
/// for FFT / waveform rendering. Snapshots copy out of the ring so the audio
/// path is never blocked by long reads.
/// </summary>
public sealed class WaveformRingBuffer
{
    private readonly float[] _buffer;
    private long _writeIndex;
    private readonly object _lock = new();

    public int Capacity => _buffer.Length;
    public long TotalSamplesWritten { get { lock (_lock) return _writeIndex; } }

    public WaveformRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new float[capacity];
    }

    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        lock (_lock)
        {
            int cap = _buffer.Length;

            // If the incoming chunk is bigger than the buffer, advance _writeIndex past
            // the samples that would be overwritten anyway and keep only the tail. The
            // rest of the method then handles the bounded write with proper wrap-around,
            // so the physical layout stays consistent with what Snapshot expects.
            if (samples.Length > cap)
            {
                int skip = samples.Length - cap;
                _writeIndex += skip;
                samples = samples.Slice(skip);
            }

            int writePos = (int)(_writeIndex % cap);
            int firstChunk = Math.Min(cap - writePos, samples.Length);
            samples.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(writePos));
            int remaining = samples.Length - firstChunk;
            if (remaining > 0)
            {
                samples.Slice(firstChunk, remaining).CopyTo(_buffer.AsSpan(0));
            }
            _writeIndex += samples.Length;
        }
    }

    /// <summary>
    /// Copy the most-recent <c>dest.Length</c> samples (or fewer, if the buffer
    /// hasn't been filled yet) into <paramref name="dest"/>. Returns the count
    /// actually written. Older samples in <paramref name="dest"/> beyond that
    /// count should be considered uninitialized.
    /// </summary>
    public int Snapshot(Span<float> dest)
    {
        if (dest.IsEmpty) return 0;
        lock (_lock)
        {
            int cap = _buffer.Length;
            long available = Math.Min(_writeIndex, cap);
            int count = (int)Math.Min(dest.Length, available);
            if (count == 0) return 0;

            long start = _writeIndex - count;
            int startPos = (int)(start % cap);
            int firstChunk = Math.Min(cap - startPos, count);

            _buffer.AsSpan(startPos, firstChunk).CopyTo(dest);
            int remaining = count - firstChunk;
            if (remaining > 0)
            {
                _buffer.AsSpan(0, remaining).CopyTo(dest.Slice(firstChunk));
            }
            return count;
        }
    }
}
