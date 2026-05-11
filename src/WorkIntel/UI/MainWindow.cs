using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Pipeline;
using WorkIntel.Transcription;

namespace WorkIntel.UI;

/// <summary>
/// Optional inspector window — FFT visualizer up top, scrolling
/// <see cref="TranscriptLog"/> below, status strip at the bottom. Closing the
/// window only hides it; the tray "Exit" item is the single source of truth
/// for app shutdown.
/// </summary>
/// <remarks>
/// MainWindow is now strictly the layout shell + event translator. The log's
/// formatting policy lives in <see cref="TranscriptLog"/>; the FFT rendering
/// lives in <see cref="FftPanel"/>. Adding a new visible event type is a
/// one-line method on <see cref="TranscriptLog"/> + a one-line subscription
/// here.
/// </remarks>
public sealed class MainWindow : Form
{
    private readonly StateManager _state;
    private readonly TranscribingAudioSink _sink;
    private readonly FftPanel _fft;
    private readonly TranscriptLog _log;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _stateLabel;
    private readonly ToolStripStatusLabel _countsLabel;
    private readonly SynchronizationContext _uiContext;

    private int _transcriptCount;
    private int _intentCount;

    private static readonly Color BgColor = Color.FromArgb(22, 23, 28);
    private static readonly Color FgColor = Color.FromArgb(220, 220, 230);
    private static readonly Color MutedColor = Color.FromArgb(140, 150, 165);

    public MainWindow(WaveformRingBuffer ring, TranscribingAudioSink sink, StateManager state)
    {
        _state = state;
        _sink = sink;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Text = "WorkIntel — live monitor";
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(560, 320);
        BackColor = BgColor;
        ForeColor = FgColor;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        KeyPreview = true;

        _fft = new FftPanel(ring) { Dock = DockStyle.Top, Height = 220 };
        _log = new TranscriptLog();

        _status = new StatusStrip
        {
            BackColor = Color.FromArgb(28, 30, 36),
            ForeColor = MutedColor,
            SizingGrip = false,
        };
        _stateLabel = new ToolStripStatusLabel($"State: {_state.Current}") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _countsLabel = new ToolStripStatusLabel("0 transcripts · 0 intents") { TextAlign = ContentAlignment.MiddleRight };
        _status.Items.Add(_stateLabel);
        _status.Items.Add(_countsLabel);

        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(45, 48, 56) };

        // Order matters with DockStyle: fill goes first so it sits between the docked siblings.
        Controls.Add(_log);     // fill
        Controls.Add(divider);  // top
        Controls.Add(_fft);     // top
        Controls.Add(_status);  // bottom

        _log.AppendBanner($"WorkIntel started at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        _log.AppendBanner("Listening on system audio loopback. Transcripts appear below as Whisper completes each segment.");
        _log.AppendBlankLine();

        _state.StateChanged += OnStateChanged;
        _sink.Transcribed += OnTranscribed;
        _sink.IntentsDetected += OnIntentsDetected;
        _sink.DispatchOutcome += OnDispatchOutcome;

        Load += (_, _) => _fft.Start();
        FormClosing += OnFormClosingOverride;
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        Post(() => _stateLabel.Text = $"State: {e.NewState}");
        _log.AppendStateChange(e.OldState, e.NewState);
    }

    private void OnTranscribed(object? sender, TranscriptionResult r)
    {
        Post(() =>
        {
            _transcriptCount++;
            UpdateCounts();
        });
        _log.AppendTranscript(r.Language, r.Text);
    }

    private void OnIntentsDetected(object? sender, IntentsDetectedEventArgs e)
    {
        Post(() =>
        {
            _intentCount += e.Intents.Count;
            UpdateCounts();
        });
        foreach (var intent in e.Intents)
            _log.AppendIntent(intent);
    }

    private void OnDispatchOutcome(object? sender, DispatchOutcomeEventArgs e)
    {
        _log.AppendDispatchOutcome(e.Intent.Kind, e.Result.Success, e.Result.Message, e.Result.RemoteUrl);
    }

    private void UpdateCounts()
    {
        _countsLabel.Text =
            $"{_transcriptCount} transcript{Plural(_transcriptCount)} · {_intentCount} intent{Plural(_intentCount)}";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private void Post(Action a)
    {
        if (IsDisposed) return;
        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            try { a(); }
            catch (Exception ex) { Log.Error("MainWindow UI update failed", ex); }
        }, null);
    }

    private void OnFormClosingOverride(object? sender, FormClosingEventArgs e)
    {
        // Closing the window is "hide", not "exit". Only the tray Exit item triggers shutdown.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _fft.Stop();
        }
    }

    public new void Show()
    {
        if (Visible)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        else
        {
            base.Show();
        }
        _fft.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.StateChanged -= OnStateChanged;
            _sink.Transcribed -= OnTranscribed;
            _sink.IntentsDetected -= OnIntentsDetected;
            _sink.DispatchOutcome -= OnDispatchOutcome;
            _fft.Dispose();
        }
        base.Dispose(disposing);
    }
}
