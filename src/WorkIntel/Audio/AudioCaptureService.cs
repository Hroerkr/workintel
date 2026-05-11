using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WorkIntel.App;

namespace WorkIntel.Audio;

/// <summary>Progress of an audio-file replay, reported by
/// <see cref="AudioCaptureService.ReplayAudioFileAsync"/>.</summary>
public readonly record struct ReplayProgress(TimeSpan Elapsed, TimeSpan Total)
{
    public double Fraction => Total > TimeSpan.Zero ? Elapsed.TotalSeconds / Total.TotalSeconds : 0d;
    public override string ToString() => $"{Elapsed:mm\\:ss} / {Total:mm\\:ss}";
}

/// <summary>
/// Owns the WASAPI loopback capture and feeds normalized mono 16 kHz float samples
/// into the <see cref="SpeechSegmenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline:
///   WasapiLoopbackCapture (IEEE float, device rate, usually 48 kHz stereo)
///     → BufferedWaveProvider (raw bytes from DataAvailable)
///     → ToSampleProvider (interleaved float)
///     → StereoToMonoSampleProvider (averaging mix-down, when needed)
///     → WdlResamplingSampleProvider (→ 16 kHz)
///     → SpeechSegmenter (energy VAD + segment buffering)
///     → IAudioSink (Whisper / intent / integrations — stubbed)
/// </para>
/// <para>
/// Sample reads happen synchronously inside <c>DataAvailable</c>: NAudio fires this
/// callback every ~10 ms with fresh bytes, and the resampler chain is pull-based.
/// We pull as many full 320-sample frames as the buffer provides, leftover staying
/// in the BufferedWaveProvider until the next callback. This keeps everything
/// single-threaded on the audio thread; the only off-thread work is the segment
/// emission inside <see cref="SpeechSegmenter"/>, which dispatches to the thread pool.
/// </para>
/// </remarks>
public sealed class AudioCaptureService : IDisposable
{
    private readonly StateManager _state;
    private readonly IAudioSink _sink;
    private readonly SpeechSegmenter _segmenter;
    private readonly WaveformRingBuffer _waveform;

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _inputBuffer;
    private ISampleProvider? _resampled;
    private MMDevice? _renderDevice;

    private readonly float[] _scratch = new float[SpeechSegmenter.FrameSamples];
    private bool _started;
    private bool _paused;
    private volatile bool _replaying;

    public bool IsCapturing => _started && !_paused;

    /// <summary>
    /// Live ring buffer of the most-recent post-resample mono samples. Exposed for UI taps
    /// (FFT visualizer, waveform display). 8192 samples ≈ 512 ms at 16 kHz — enough for
    /// a 1024-sample FFT window with smoothing headroom.
    /// </summary>
    public WaveformRingBuffer Waveform => _waveform;

    public AudioCaptureService(StateManager state, IAudioSink sink, EnergyVad? vad = null)
    {
        _state = state;
        _sink = sink;
        _segmenter = new SpeechSegmenter(vad ?? new EnergyVad(), sink, state);
        _waveform = new WaveformRingBuffer(8192);
        _state.StateChanged += OnStateChanged;
    }

    public void Start()
    {
        if (_started) return;

        var enumerator = new MMDeviceEnumerator();
        _renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Log.Info($"loopback target: {_renderDevice.FriendlyName} ({_renderDevice.AudioClient.MixFormat.SampleRate} Hz, {_renderDevice.AudioClient.MixFormat.Channels} ch)");

        _capture = new WasapiLoopbackCapture(_renderDevice);
        var inFmt = _capture.WaveFormat;

        _inputBuffer = new BufferedWaveProvider(inFmt)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            ReadFully = false
        };

        ISampleProvider source = _inputBuffer.ToSampleProvider();
        if (inFmt.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (inFmt.Channels > 2)
        {
            // Multi-channel devices (e.g. 5.1 loopback): downmix front-L/R only as a sane default.
            // WdlResamplingSampleProvider doesn't downmix; so we wrap with a custom mono provider.
            source = new MultiChannelToMonoSampleProvider(source, inFmt.Channels);
        }

        _resampled = new WdlResamplingSampleProvider(source, SpeechSegmenter.TargetSampleRate);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _started = true;
        Log.Info("audio capture started");
    }

    public void Stop()
    {
        if (!_started) return;
        try { _capture?.StopRecording(); }
        catch (Exception ex) { Log.Warn($"stop recording failed: {ex.Message}"); }
        _started = false;
        _segmenter.Reset();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Suppress live capture while a replay is running so the user only sees
        // the file's audio in the FFT / transcripts. Avoids confusing mix-in.
        if (_paused || _replaying || _inputBuffer is null || _resampled is null) return;

        try
        {
            _inputBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            int read;
            do
            {
                read = _resampled.Read(_scratch, 0, _scratch.Length);
                if (read > 0)
                {
                    var span = _scratch.AsSpan(0, read);
                    _waveform.WriteSamples(span);          // tap for UI / FFT
                    _segmenter.Push(span, DateTimeOffset.UtcNow);
                }
            }
            while (read == _scratch.Length); // keep pulling while full frames are available
        }
        catch (Exception ex)
        {
            Log.Error("OnDataAvailable failed", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Error("recording stopped with exception", e.Exception);
        else Log.Info("recording stopped");
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        // Pause/Resume mirroring: when state goes to Paused, drop the audio path.
        bool shouldPause = e.NewState == AppState.Paused;
        if (shouldPause == _paused) return;
        _paused = shouldPause;
        if (_paused)
        {
            _segmenter.Reset();
            _inputBuffer?.ClearBuffer();
            Log.Info("capture paused");
        }
        else
        {
            Log.Info("capture resumed");
        }
    }

    /// <summary>
    /// Feed an audio file (WAV / MP3 / AIFF / WMA — anything <see cref="AudioFileReader"/>
    /// understands) through the same pipeline that live loopback would, in real time.
    /// Live capture is suppressed for the duration so the user sees only the file.
    /// </summary>
    /// <remarks>
    /// Useful for self-testing the full transcribe → intent → dispatch pipeline without
    /// needing real audio activity. Real-time playback rate makes the FFT visualisation
    /// meaningful; if you want as-fast-as-possible (for batch testing), set
    /// <paramref name="realTime"/> to <c>false</c>.
    /// </remarks>
    public async Task ReplayAudioFileAsync(
        string path,
        IProgress<ReplayProgress>? progress = null,
        bool realTime = true,
        CancellationToken ct = default)
    {
        if (_replaying) throw new InvalidOperationException("Replay already in progress.");
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        _replaying = true;
        _segmenter.Reset();
        try
        {
            Log.Info($"replay starting: {path}");
            using var reader = new AudioFileReader(path);
            var totalDuration = reader.TotalTime;

            ISampleProvider source = reader;
            if (reader.WaveFormat.Channels == 2)
            {
                source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
            }
            else if (reader.WaveFormat.Channels > 2)
            {
                source = new MultiChannelToMonoSampleProvider(source, reader.WaveFormat.Channels);
            }

            ISampleProvider toFeed = source.WaveFormat.SampleRate == SpeechSegmenter.TargetSampleRate
                ? source
                : new WdlResamplingSampleProvider(source, SpeechSegmenter.TargetSampleRate);

            var buffer = new float[SpeechSegmenter.FrameSamples];
            long totalSamples = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                int read = toFeed.Read(buffer, 0, buffer.Length);
                if (read == 0) break;

                var span = buffer.AsSpan(0, read);
                _waveform.WriteSamples(span);
                _segmenter.Push(span, DateTimeOffset.UtcNow);

                totalSamples += read;
                var elapsedAudio = TimeSpan.FromSeconds((double)totalSamples / SpeechSegmenter.TargetSampleRate);
                progress?.Report(new ReplayProgress(elapsedAudio, totalDuration));

                if (realTime)
                {
                    var pacingDelta = elapsedAudio - sw.Elapsed;
                    if (pacingDelta > TimeSpan.FromMilliseconds(2))
                        await Task.Delay(pacingDelta, ct).ConfigureAwait(false);
                }
            }

            Log.Info($"replay complete: {totalSamples} samples ({TimeSpan.FromSeconds((double)totalSamples / SpeechSegmenter.TargetSampleRate).TotalSeconds:F1}s) in {sw.Elapsed.TotalSeconds:F1}s wall");
        }
        finally
        {
            _replaying = false;
            _segmenter.Reset(); // close any in-progress segment so live capture starts clean
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* swallow */ }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _renderDevice?.Dispose();
        _renderDevice = null;
        _state.StateChanged -= OnStateChanged;
    }
}

/// <summary>
/// Naive multi-channel → mono mix. Averages all channels per frame.
/// Adequate for non-music loopback (meetings, calls). Replace with a proper
/// downmix matrix if surround content quality matters.
/// </summary>
internal sealed class MultiChannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buf = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public MultiChannelToMonoSampleProvider(ISampleProvider source, int channels)
    {
        if (channels < 2) throw new ArgumentException("channels must be >= 2", nameof(channels));
        _source = source;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_buf.Length < needed) _buf = new float[needed];
        int read = _source.Read(_buf, 0, needed);
        int monoSamples = read / _channels;
        for (int i = 0; i < monoSamples; i++)
        {
            float sum = 0f;
            int baseIdx = i * _channels;
            for (int c = 0; c < _channels; c++) sum += _buf[baseIdx + c];
            buffer[offset + i] = sum / _channels;
        }
        return monoSamples;
    }
}
