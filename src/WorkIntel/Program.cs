using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WorkIntel.App;
using WorkIntel.Audio;
using WorkIntel.Configuration;
using WorkIntel.Integrations;
using WorkIntel.Intent;
using WorkIntel.Transcription;
using WorkIntel.UI;

namespace WorkIntel;

internal static class Program
{
    /// <summary>
    /// Composition root. Loads encrypted preferences, builds the audio /
    /// transcription / intent / dispatcher chain from them, hands hot-reloadable
    /// references (StateManager, EnergyVad, IntegrationDispatcher) to the tray
    /// context so the Settings dialog can reapply changes without restart.
    /// </summary>
    [STAThread]
    private static void Main()
    {
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

        // Whisper: local STT (no data leaves the machine).
        var whisperStore = new WhisperModelStore(prefs.Whisper.ModelDirectory);
        var transcriber = new WhisperTranscriber(prefs.Whisper, whisperStore);

        // Phi-3.5: local intent extraction (no data leaves the machine).
        var llmStore = new LocalLlmModelStore(prefs.Llm.ModelDirectory);
        var intentExtractor = new LocalIntentExtractor(prefs.Llm, llmStore);

        // Integration dispatcher: outbound calls only to user-configured workspaces.
        var dispatcher = new IntegrationDispatcher(
            new HarvestClient(prefs.Secrets.Harvest ?? new HarvestSecrets()),
            new TrelloClient(prefs.Secrets.Trello ?? new TrelloSecrets()),
            new SlackClient(prefs.Secrets.Slack ?? new SlackSecrets()));

        // Sink: transcribe → extract intents → dispatch.
        var audioSink = new TranscribingAudioSink(
            transcriber: transcriber,
            intentExtractor: intentExtractor,
            dispatcher: dispatcher);

        var captureService = new AudioCaptureService(stateManager, audioSink, vad);

        // Tray context owns the window factory and the hot-reload handles.
        using var trayContext = new TrayApplicationContext(
            stateManager,
            captureService,
            vad,
            dispatcher,
            prefs,
            mainWindowFactory: () => new MainWindow(captureService.Waveform, audioSink, stateManager));

        // Warm up both models in parallel on background threads. Capture starts immediately;
        // segments queue at the respective process locks until each model is ready.
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
        dispatcher.Dispose();
        stateManager.Dispose();
    }
}
