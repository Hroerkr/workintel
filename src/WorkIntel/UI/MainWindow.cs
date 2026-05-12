using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Pipeline;
using WorkIntel.Tasks;
using WorkIntel.Transcription;

namespace WorkIntel.UI;

/// <summary>
/// Live monitor — FFT visualizer up top, tabbed lower half (Tasks + Live Log),
/// status strip at the bottom. Closing the window hides it; only the tray
/// "Exit" item shuts the app down.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly StateManager _state;
    private readonly TranscribingAudioSink _sink;
    private readonly RemoteTaskStore _taskStore;
    private readonly FftPanel _fft;
    private readonly TranscriptLog _log;
    private readonly TaskListPanel _tasks;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _stateLabel;
    private readonly ToolStripStatusLabel _backendLabel;
    private readonly SynchronizationContext _uiContext;

    private int _transcriptCount;
    private int _taskCount;

    private static readonly Color BgColor = Color.FromArgb(22, 23, 28);
    private static readonly Color FgColor = Color.FromArgb(220, 220, 230);
    private static readonly Color MutedColor = Color.FromArgb(140, 150, 165);

    public MainWindow(
        WaveformRingBuffer ring,
        TranscribingAudioSink sink,
        StateManager state,
        RemoteTaskStore taskStore)
    {
        _state = state;
        _sink = sink;
        _taskStore = taskStore;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Text = "WorkIntel — live monitor";
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(720, 420);
        BackColor = BgColor;
        ForeColor = FgColor;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _fft = new FftPanel(ring) { Dock = DockStyle.Top, Height = 180 };
        _log = new TranscriptLog();
        _tasks = new TaskListPanel(taskStore);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = BgColor,
            ForeColor = FgColor,
            Appearance = TabAppearance.Normal,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(120, 26),
            SizeMode = TabSizeMode.Fixed,
        };
        tabs.DrawItem += DrawTabHeader;

        var tasksTab = new TabPage("Tasks") { BackColor = BgColor, ForeColor = FgColor };
        tasksTab.Controls.Add(_tasks);
        tabs.TabPages.Add(tasksTab);

        var logTab = new TabPage("Live log") { BackColor = BgColor, ForeColor = FgColor };
        logTab.Controls.Add(_log);
        tabs.TabPages.Add(logTab);

        _status = new StatusStrip
        {
            BackColor = Color.FromArgb(28, 30, 36),
            ForeColor = MutedColor,
            SizingGrip = false,
        };
        _stateLabel = new ToolStripStatusLabel($"State: {_state.Current}") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _backendLabel = new ToolStripStatusLabel($"Backend: {taskStore.Endpoint}") { TextAlign = ContentAlignment.MiddleRight };
        _status.Items.Add(_stateLabel);
        _status.Items.Add(_backendLabel);

        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(45, 48, 56) };

        Controls.Add(tabs);     // fill
        Controls.Add(divider);  // top
        Controls.Add(_fft);     // top
        Controls.Add(_status);  // bottom

        _log.AppendBanner($"WorkIntel started at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        _log.AppendBanner($"Backend: {taskStore.Endpoint}");
        _log.AppendBlankLine();

        _state.StateChanged += OnStateChanged;
        _sink.Transcribed += OnTranscribed;
        _sink.TasksExtracted += OnTasksExtracted;
        _taskStore.StreamStateChanged += OnBackendState;

        Load += (_, _) => _fft.Start();
        FormClosing += OnFormClosingOverride;
    }

    private static void DrawTabHeader(object? sender, DrawItemEventArgs e)
    {
        var tabs = (TabControl)sender!;
        var page = tabs.TabPages[e.Index];
        var rect = tabs.GetTabRect(e.Index);
        var selected = tabs.SelectedIndex == e.Index;

        var bg = selected ? Color.FromArgb(40, 44, 52) : Color.FromArgb(28, 30, 36);
        var fg = selected ? Color.FromArgb(220, 220, 230) : Color.FromArgb(140, 150, 165);
        using var bgBrush = new SolidBrush(bg);
        using var fgBrush = new SolidBrush(fg);
        e.Graphics.FillRectangle(bgBrush, rect);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(page.Text, tabs.Font, fgBrush, rect, fmt);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        Post(() => _stateLabel.Text = $"State: {e.NewState}");
        _log.AppendStateChange(e.OldState, e.NewState);
    }

    private void OnTranscribed(object? sender, TranscriptionResult r)
    {
        Interlocked.Increment(ref _transcriptCount);
        _log.AppendTranscript(r.Language, r.Text);
    }

    private void OnTasksExtracted(object? sender, TasksExtractedEventArgs e)
    {
        Interlocked.Add(ref _taskCount, e.Tasks.Count);
        foreach (var t in e.Tasks) _log.AppendTask(t);
    }

    private void OnBackendState(object? sender, StreamState state)
    {
        Post(() =>
        {
            var label = state switch
            {
                StreamState.Connecting   => $"Backend: connecting to {_taskStore.Endpoint}",
                StreamState.Connected    => $"Backend: live ({_taskStore.Endpoint})",
                StreamState.Disconnected => $"Backend: disconnected ({_taskStore.Endpoint})",
                _ => _backendLabel.Text,
            };
            _backendLabel.Text = label;
        });
    }

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
            _sink.TasksExtracted -= OnTasksExtracted;
            _taskStore.StreamStateChanged -= OnBackendState;
            _fft.Dispose();
        }
        base.Dispose(disposing);
    }
}
