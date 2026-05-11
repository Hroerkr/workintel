using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NAudio.Dsp;
using WorkIntel.Audio;

namespace WorkIntel.UI;

/// <summary>
/// Custom panel that polls a <see cref="WaveformRingBuffer"/> at ~30 Hz, runs an
/// FFT, and draws the magnitude spectrum as log-spaced bars. Single-threaded —
/// the polling timer, FFT computation, and paint all run on the UI thread.
/// </summary>
public sealed class FftPanel : Panel
{
    private const int FftSize = 1024;
    private const int FftBins = FftSize / 2;
    private const int LogN = 10; // log2(1024)
    private const int BandCount = 64;
    private const float SmoothingDecay = 0.65f; // how much old peak persists each frame

    // 16 kHz Nyquist = 8000 Hz. 80–7800 Hz spans most speech energy.
    private const float MinHz = 80f;
    private const float MaxHz = 7800f;
    private const int SampleRate = 16_000;

    private readonly WaveformRingBuffer _ring;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _magnitudes = new float[FftBins];
    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _smoothed = new float[BandCount];
    private readonly int[] _bandStartBin = new int[BandCount + 1]; // band[i] covers [start[i], start[i+1])

    public FftPanel(WaveformRingBuffer ring)
    {
        _ring = ring;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(15, 16, 20);
        ForeColor = Color.FromArgb(220, 220, 230);

        ComputeBandBins();

        _timer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        _timer.Tick += (_, _) =>
        {
            ComputeFrame();
            Invalidate();
        };
    }

    public void Start()
    {
        if (!_timer.Enabled) _timer.Start();
    }

    public void Stop()
    {
        if (_timer.Enabled) _timer.Stop();
    }

    private void ComputeBandBins()
    {
        // Logarithmic frequency spacing from MinHz to MaxHz.
        double logMin = Math.Log10(MinHz);
        double logMax = Math.Log10(MaxHz);
        double step = (logMax - logMin) / BandCount;
        for (int i = 0; i <= BandCount; i++)
        {
            double hz = Math.Pow(10, logMin + i * step);
            int bin = (int)Math.Round(hz * FftSize / SampleRate);
            if (bin < 1) bin = 1;
            if (bin > FftBins) bin = FftBins;
            _bandStartBin[i] = bin;
        }
    }

    private void ComputeFrame()
    {
        int got = _ring.Snapshot(_samples);
        if (got < FftSize)
        {
            // Not enough samples yet; decay current bands toward zero.
            for (int i = 0; i < BandCount; i++)
                _smoothed[i] *= SmoothingDecay;
            return;
        }

        // Window + load FFT input.
        for (int i = 0; i < FftSize; i++)
        {
            double w = FastFourierTransform.HannWindow(i, FftSize);
            _fftBuffer[i].X = (float)(_samples[i] * w);
            _fftBuffer[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, LogN, _fftBuffer);

        // Magnitudes (positive frequencies only).
        for (int i = 0; i < FftBins; i++)
        {
            float re = _fftBuffer[i].X;
            float im = _fftBuffer[i].Y;
            _magnitudes[i] = MathF.Sqrt(re * re + im * im);
        }

        // Aggregate FFT bins into log-spaced bands (max within range).
        for (int b = 0; b < BandCount; b++)
        {
            int lo = _bandStartBin[b];
            int hi = _bandStartBin[b + 1];
            if (hi <= lo) hi = lo + 1;

            float peak = 0f;
            for (int k = lo; k < hi && k < FftBins; k++)
            {
                if (_magnitudes[k] > peak) peak = _magnitudes[k];
            }
            _bands[b] = peak;
        }

        // Convert magnitude → normalized dB-ish display value (0..1) and apply smoothing.
        // Empirically magnitudes for typical speech sit in [0.01, 5.0]; clamp to dB scale.
        for (int b = 0; b < BandCount; b++)
        {
            float mag = _bands[b];
            float db = 20f * MathF.Log10(mag + 1e-6f);
            // Map [-60dB, 0dB] → [0, 1].
            float v = (db + 60f) / 60f;
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;

            // Peak-hold smoothing: instant rise, gradual fall.
            if (v > _smoothed[b]) _smoothed[b] = v;
            else _smoothed[b] = _smoothed[b] * SmoothingDecay + v * (1f - SmoothingDecay);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(BackColor);

        int w = ClientSize.Width;
        int h = ClientSize.Height;
        if (w < 8 || h < 8) return;

        const int paddingLeft = 8, paddingRight = 8, paddingTop = 8, paddingBottom = 18;
        int plotW = w - paddingLeft - paddingRight;
        int plotH = h - paddingTop - paddingBottom;
        if (plotW < BandCount || plotH < 8) return;

        // Reference grid (horizontal lines at quarter steps).
        using (var gridPen = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
        {
            for (int q = 1; q <= 3; q++)
            {
                int y = paddingTop + plotH * q / 4;
                g.DrawLine(gridPen, paddingLeft, y, paddingLeft + plotW, y);
            }
        }

        float bandSpacing = (float)plotW / BandCount;
        float barWidth = bandSpacing * 0.78f;

        for (int b = 0; b < BandCount; b++)
        {
            float v = _smoothed[b];
            int barH = (int)(v * plotH);
            if (barH < 1 && v > 0.01f) barH = 1;

            float x = paddingLeft + b * bandSpacing + (bandSpacing - barWidth) / 2f;
            float y = paddingTop + plotH - barH;

            Color top = LerpColor(v);
            Color bottom = Color.FromArgb(220, top.R / 3, top.G / 3, top.B / 3);

            using var brush = new LinearGradientBrush(
                new RectangleF(x, y, barWidth, barH + 1),
                top, bottom, LinearGradientMode.Vertical);
            g.FillRectangle(brush, x, y, barWidth, barH);
        }

        // Frequency-axis labels.
        using var labelFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        using var labelBrush = new SolidBrush(Color.FromArgb(140, 200, 200, 220));
        DrawFreqLabel(g, labelFont, labelBrush, paddingLeft, paddingTop + plotH, "100");
        DrawFreqLabel(g, labelFont, labelBrush, paddingLeft + plotW / 4, paddingTop + plotH, "300");
        DrawFreqLabel(g, labelFont, labelBrush, paddingLeft + plotW / 2, paddingTop + plotH, "1k");
        DrawFreqLabel(g, labelFont, labelBrush, paddingLeft + 3 * plotW / 4, paddingTop + plotH, "3k");
        DrawFreqLabel(g, labelFont, labelBrush, paddingLeft + plotW - 12, paddingTop + plotH, "8k");
    }

    private static void DrawFreqLabel(Graphics g, Font font, Brush brush, int x, int y, string text)
        => g.DrawString(text, font, brush, x - 6, y + 2);

    /// <summary>Cool→warm gradient by intensity 0..1.</summary>
    private static Color LerpColor(float v)
    {
        // 0.0 = deep blue, 0.5 = teal/green, 0.8 = amber, 1.0 = red
        v = Math.Clamp(v, 0f, 1f);
        if (v < 0.5f)
        {
            float t = v / 0.5f;
            return Color.FromArgb(255,
                (int)Lerp(40, 80, t),
                (int)Lerp(120, 220, t),
                (int)Lerp(220, 200, t));
        }
        else
        {
            float t = (v - 0.5f) / 0.5f;
            return Color.FromArgb(255,
                (int)Lerp(80, 235, t),
                (int)Lerp(220, 100, t),
                (int)Lerp(200, 70, t));
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
