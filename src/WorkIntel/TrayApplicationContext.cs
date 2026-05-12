using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Configuration;
using WorkIntel.Integrations;
using WorkIntel.Tray;
using WorkIntel.UI;

namespace WorkIntel;

/// <summary>
/// Top-level WinForms ApplicationContext: owns the NotifyIcon, tray menu, lazy
/// main window, and the Settings dialog flow (load → edit → save → hot-reload
/// what we can, prompt restart for what we can't).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly StateManager _state;
    private readonly AudioCaptureService _capture;
    private readonly EnergyVad _vad;
    // Dispatcher is vestigial — kept nullable so Program.cs can pass null while
    // the legacy Settings flow still touches it for the integration tabs.
    private readonly IntegrationDispatcher? _dispatcher;
    private readonly Func<MainWindow> _mainWindowFactory;
    private readonly NotifyIcon _notifyIcon;
    private readonly IReadOnlyDictionary<AppState, Icon> _icons;
    private readonly SynchronizationContext? _uiContext;

    private AppPreferences _currentPrefs;

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _pauseResumeItem = null!;
    private ToolStripMenuItem _showWindowItem = null!;
    private ToolStripMenuItem _settingsItem = null!;
    private MainWindow? _mainWindow;

    public TrayApplicationContext(
        StateManager state,
        AudioCaptureService capture,
        EnergyVad vad,
        IntegrationDispatcher? dispatcher,
        AppPreferences currentPrefs,
        Func<MainWindow> mainWindowFactory)
    {
        _state = state;
        _capture = capture;
        _vad = vad;
        _dispatcher = dispatcher;
        _currentPrefs = currentPrefs;
        _mainWindowFactory = mainWindowFactory;
        _icons = TrayIconFactory.CreateAll();
        _uiContext = SynchronizationContext.Current;

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[_state.Current],
            Text = BuildTooltip(_state.Current),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        _state.StateChanged += OnStateChanged;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem($"Status: {_state.Current}") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        _showWindowItem = new ToolStripMenuItem("Show live monitor…");
        _showWindowItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.W;
        _showWindowItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(_showWindowItem);

        _pauseResumeItem = new ToolStripMenuItem(LabelForPauseToggle(_state.Current));
        _pauseResumeItem.Click += (_, _) => _state.TogglePause();
        menu.Items.Add(_pauseResumeItem);

        menu.Items.Add(new ToolStripSeparator());

        var replayItem = new ToolStripMenuItem("Replay audio file…");
        replayItem.ToolTipText = "Feed a WAV/MP3 through the live pipeline (useful for testing without real audio activity)";
        replayItem.Click += (_, _) => OnReplayAudioClicked();
        menu.Items.Add(replayItem);

        var openLogsItem = new ToolStripMenuItem("Open log folder");
        openLogsItem.Click += (_, _) => OpenLogFolder();
        menu.Items.Add(openLogsItem);

        _settingsItem = new ToolStripMenuItem("Settings…");
        _settingsItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
        _settingsItem.Click += (_, _) => ShowSettingsDialog();
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var aboutItem = new ToolStripMenuItem("About WorkIntel…");
        aboutItem.Click += (_, _) => MessageBox.Show(
            "WorkIntel — local audio → task automation\n\n" +
            "Audio capture, Whisper transcription and Phi-3.5 intent extraction all run on this machine. " +
            "Credentials are encrypted on disk with Windows DPAPI.",
            "WorkIntel",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        menu.Items.Add(aboutItem);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || _mainWindow.IsDisposed)
            _mainWindow = _mainWindowFactory();
        _mainWindow.Show();
        _mainWindow.WindowState = FormWindowState.Normal;
        _mainWindow.BringToFront();
        _mainWindow.Activate();
    }

    private void ShowSettingsDialog()
    {
        var previous = _currentPrefs;
        using var dialog = new SettingsDialog(_currentPrefs);
        var owner = _mainWindow is { IsDisposed: false, Visible: true } ? _mainWindow : null;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != DialogResult.OK) return;

        var updated = dialog.Result;
        try
        {
            PreferencesStore.Save(updated);
        }
        catch (Exception ex)
        {
            Log.Error("preferences save failed", ex);
            MessageBox.Show(
                $"Could not save preferences:\n\n{ex.Message}",
                "WorkIntel — settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _currentPrefs = updated;
        ApplyHotReloadable(updated);

        if (RequiresRestart(previous, updated))
        {
            var prompt = MessageBox.Show(
                "Whisper or LLM model changes will take effect after a restart. Restart WorkIntel now?",
                "WorkIntel — restart needed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (prompt == DialogResult.Yes) RestartApp();
        }
    }

    private void ApplyHotReloadable(AppPreferences p)
    {
        try
        {
            _state.IdleAfter = TimeSpan.FromSeconds(p.Capture.IdleAfterSeconds);
            _vad.ActivationDb   = p.Capture.VadActivationDb;
            _vad.DeactivationDb = p.Capture.VadDeactivationDb;
            _vad.Hangover       = TimeSpan.FromMilliseconds(p.Capture.VadHangoverMs);
            _dispatcher?.Reload(p.Secrets);
            Log.Info("hot-reloadable preferences applied (capture thresholds, integration credentials)");
        }
        catch (Exception ex)
        {
            Log.Error("hot-reload partially failed", ex);
        }
    }

    private static bool RequiresRestart(AppPreferences a, AppPreferences b)
    {
        if (a.Whisper.Model != b.Whisper.Model) return true;
        if (a.Whisper.Language != b.Whisper.Language) return true;
        if (a.Whisper.Translate != b.Whisper.Translate) return true;
        if (a.Whisper.InitialPrompt != b.Whisper.InitialPrompt) return true;
        if (a.Whisper.Threads != b.Whisper.Threads) return true;
        if (a.Llm.Model != b.Llm.Model) return true;
        if (a.Llm.ContextSize != b.Llm.ContextSize) return true;
        if (a.Llm.Threads != b.Llm.Threads) return true;
        if (a.Llm.GpuLayerCount != b.Llm.GpuLayerCount) return true;
        // Temperature is technically rebuildable on the next inference, but we apply on restart for simplicity.
        if (Math.Abs(a.Llm.Temperature - b.Llm.Temperature) > 0.001f) return true;
        return false;
    }

    private void RestartApp()
    {
        try
        {
            var module = Process.GetCurrentProcess().MainModule;
            if (module is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = module.FileName,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("restart failed; please relaunch WorkIntel manually", ex);
        }
        ExitThread();
    }

    private static string LabelForPauseToggle(AppState state) =>
        state == AppState.Paused ? "Resume capture" : "Pause capture";

    private static string BuildTooltip(AppState state) => state switch
    {
        AppState.Active => "WorkIntel — listening (active)",
        AppState.Idle   => "WorkIntel — listening (idle)",
        AppState.Paused => "WorkIntel — paused",
        _ => "WorkIntel"
    };

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        if (_uiContext is null)
        {
            ApplyStateToUi(e.NewState);
            return;
        }
        _uiContext.Post(_ => ApplyStateToUi(e.NewState), null);
    }

    private void ApplyStateToUi(AppState state)
    {
        if (_icons.TryGetValue(state, out var icon))
            _notifyIcon.Icon = icon;
        _notifyIcon.Text = BuildTooltip(state);
        _statusItem.Text = $"Status: {state}";
        _pauseResumeItem.Text = LabelForPauseToggle(state);
    }

    private void OnReplayAudioClicked()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Replay audio file through the WorkIntel pipeline",
            Filter = "Audio files (*.wav;*.mp3;*.m4a;*.aiff;*.wma)|*.wav;*.mp3;*.m4a;*.aiff;*.wma|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog() != DialogResult.OK) return;

        var path = ofd.FileName;

        // Pop the live monitor so the user can watch the FFT and the transcript appear.
        ShowMainWindow();

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ReplayProgress>(p =>
                    Log.Debug($"replay progress: {p}"));
                await _capture.ReplayAudioFileAsync(path, progress, realTime: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("audio replay failed", ex);
            }
        });
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = Log.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("failed to open log folder", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.StateChanged -= OnStateChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            foreach (var icon in _icons.Values) icon.Dispose();
            _mainWindow?.Dispose();
        }
        base.Dispose(disposing);
    }
}
