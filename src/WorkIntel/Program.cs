using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Configuration;
using WorkIntel.Intent;
using WorkIntel.Tasks;
using WorkIntel.Transcription;
using WorkIntel.UI;

namespace WorkIntel;

internal static class Program
{
    // Phase-1 backend address. Move to AppPreferences.Backend once we wire the Settings tab.
    private const string DefaultBackendEndpoint = "http://localhost:5000";

    /// <summary>
    /// Composition root. Loads encrypted preferences, builds the audio →
    /// transcription → task-extraction → remote task store chain, hands
    /// hot-reloadable references to the tray.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // gRPC over cleartext HTTP/2 needs this AppContext switch BEFORE any
        // channel is constructed. Localhost-only for Phase 1; once we ship TLS
        // we can drop the flag.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        using var mutex = new Mutex(initiallyOwned: true, name: "WorkIntel.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WorkIntel is already running (check the system tray).", "WorkIntel",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Domain unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Log.Info("WorkIntel starting up");

        // Encrypted preferences (DPAPI) + env-var overlay + legacy config.json migration.
        var prefs = PreferencesStore.Load();

        var stateManager = new StateManager
        {
            IdleAfter = TimeSpan.FromSeconds(prefs.Capture.IdleAfterSeconds),
        };

        var vad = new EnergyVad
        {
            ActivationDb   = prefs.Capture.VadActivationDb,
            DeactivationDb = prefs.Capture.VadDeactivationDb,
            Hangover       = TimeSpan.FromMilliseconds(prefs.Capture.VadHangoverMs),
        };

        // Whisper: local STT.
        var whisperStore = new WhisperModelStore(prefs.Whisper.ModelDirectory);
        var transcriber = new WhisperTranscriber(prefs.Whisper, whisperStore);

        // Phi-3.5: local task extraction.
        var llmStore = new LocalLlmModelStore(prefs.Llm.ModelDirectory);
        var intentExtractor = new LocalIntentExtractor(prefs.Llm, llmStore);

        // Remote task store: gRPC client + background event subscription.
        var taskStore = new RemoteTaskStore(DefaultBackendEndpoint);

        var audioSink = new TranscribingAudioSink(
            transcriber: transcriber,
            extractor: intentExtractor,
            taskStore: taskStore);

        var captureService = new AudioCaptureService(stateManager, audioSink, vad);

        using var trayContext = new TrayApplicationContext(
            stateManager,
            captureService,
            vad,
            dispatcher: null,  // legacy outbound dispatcher path no longer wired
            prefs,
            mainWindowFactory: () => new MainWindow(captureService.Waveform, audioSink, stateManager, taskStore));

        // Warm up both models on background threads. Capture starts immediately.
        _ = Task.Run(async () =>
        {
            try { await transcriber.WarmupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("whisper warmup failed", ex); }
        });
        _ = Task.Run(async () =>
        {
            try { await intentExtractor.WarmupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("local LLM warmup failed", ex); }
        });

        // Start the gRPC event subscription early — even before the main window
        // opens, the TaskListPanel will already have live data when shown.
        taskStore.StartEventStream();

        try
        {
            captureService.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start audio capture", ex);
            MessageBox.Show(
                $"Could not start audio capture:\n\n{ex.Message}\n\nWorkIntel will run in a degraded state. Check the log for details.",
                "WorkIntel",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        Application.Run(trayContext);

        Log.Info("WorkIntel shutting down");
        captureService.Dispose();
        transcriber.DisposeAsync().AsTask().GetAwaiter().GetResult();
        intentExtractor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        taskStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
        stateManager.Dispose();
    }
}
