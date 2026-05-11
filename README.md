# WorkIntel

A personal work-intelligence stack. Two input streams — system audio (live
loopback transcription) and Slack DMs — feed a local LLM that extracts task
candidates, which land in a Postgres database via a containerized gRPC API.
A triage UI lets you Include / Exclude / Remove each task, and Included tasks
can be exported by email to a Trello / Jira / Atlassian board.

> **Status mid-pivot.** Audio path + Whisper + local LLM intent extraction are
> working end-to-end against an in-process pipeline. The architecture is being
> reworked to land tasks in a Postgres-backed task store fronted by a gRPC API
> (this section is currently being built — see `src/WorkIntel.Api/`,
> `src/WorkIntel.Data/`, `src/WorkIntel.Contracts/`, `deploy/`). The Slack
> *listener* (incoming DMs, not outbound posts) and the desktop client's
> rewire to the new task store come next.

## Architecture

```
┌──────────────────────────────────────────┐
│ WorkIntel (Windows desktop tray app)     │
│                                          │
│  Audio loopback ──┐                      │
│  Slack DMs      ──┤── local LLM ──┐      │
│                   │ (Phi-3.5 Mini) │      │
│                   │                ▼      │
│  Triage UI ◄──────┴──── Task candidates  │
│   (Include / Exclude / Remove / Export)  │
└────────────────┬─────────────────────────┘
                 │ gRPC (HTTP/2, localhost:5000)
                 ▼
┌──────────────────────────────────────────┐
│ WorkIntel.Api (Docker container)         │
│  - CRUD for tasks                        │
│  - Server-side stream of task events     │
│  - Email export to configured boards     │
└────────────────┬─────────────────────────┘
                 │ SQL
                 ▼
┌──────────────────────────────────────────┐
│ Postgres 16 (Docker container)           │
└──────────────────────────────────────────┘
```

Why this shape:

- **Postgres**, not SQLite — data outlives desktop reinstalls, multi-user is a
  config flip not a schema migration, standard admin tooling works (pgAdmin,
  DBeaver, `psql`).
- **gRPC** for the API surface — server-side streaming for live task updates
  in the UI without polling, schema-as-code via `.proto`, generated client
  code for free.
- **Email export** instead of per-product API integrations — Trello and Jira
  both support "email to board" natively. One outbound mechanism, no per-vendor
  auth ceremony or rate-limit handling.
- **Local LLM** — all transcript and message processing stays on the device.
  The only outbound network call after setup is the email export (and DNS for
  Slack DM events).

> **Status:** Audio capture + tray + live monitor (FFT + transcript log) + local
> Whisper transcription + local Phi-3.5 intent extraction + Harvest / Trello /
> Slack dispatchers + DPAPI-encrypted preferences with a tabbed settings dialog
> are all live. The only outbound network calls are to your own integration
> workspaces, on a detected intent.

---

## Stack

- **.NET 8 / WinForms** — single tray executable.
- **NAudio 2.2.1** — `WasapiLoopbackCapture` for system-wide audio + the WDL resampler.
- **Whisper.net** — local transcription (next phase).
- **Anthropic Claude API** — task / intent extraction (next phase).
- **Harvest v2 / Trello / Slack Web** — action dispatch targets (next phase).

## Project layout

```
src/WorkIntel/
├── Program.cs                      Entry point + single-instance guard
├── TrayApplicationContext.cs       NotifyIcon, menu, state ↔ UI marshalling
├── App/
│   ├── AppState.cs                 Active / Idle / Paused
│   ├── StateManager.cs             State machine + StateChanged event
│   └── Log.cs                      File + Trace logger (%LOCALAPPDATA%\WorkIntel)
├── Audio/
│   ├── AudioCaptureService.cs      WASAPI loopback → mono → 16 kHz → segmenter
│   ├── EnergyVad.cs                Two-threshold + hangover VAD (swap for Silero later)
│   ├── SpeechSegmenter.cs          Frames audio, emits SpeechSegment per utterance
│   ├── SpeechSegment.cs            Mono 16 kHz float PCM payload (Whisper-ready)
│   ├── IAudioSink.cs               Downstream consumer interface
│   └── LoggingAudioSink.cs         Fallback sink (logs each segment, used pre-Whisper)
├── Transcription/
│   ├── WhisperOptions.cs           Model size, language, threads, prompt config
│   ├── WhisperModelStore.cs        Locate / download / atomically install ggml models
│   ├── WhisperTranscriber.cs       ITranscriber impl; lazy model load, serialized inference
│   └── TranscribingAudioSink.cs    IAudioSink → ITranscriber → IIntentExtractor → dispatcher
├── Intent/
│   ├── LocalLlmOptions.cs          Model variant, ctx size, threads, gpu offload, sampling
│   ├── LocalLlmModelStore.cs       Locate / download / atomically install gguf weights
│   ├── IntentSchema.cs             Well-known intent kinds + few-shot system prompt
│   └── LocalIntentExtractor.cs     IIntentExtractor via LLamaSharp + Phi-3.5 Mini
├── Configuration/
│   ├── DpapiVault.cs               ProtectedData.Protect/Unprotect with app-scoped entropy
│   ├── AppPreferences.cs           Root settings record (capture / whisper / llm / secrets)
│   └── PreferencesStore.cs         Load / save DPAPI-encrypted blob, env-var overlay, legacy migration
├── Integrations/
│   ├── IntegrationSecrets.cs       Config records for Harvest / Trello / Slack
│   ├── SecretsLoader.cs            Deprecated shim — delegates to PreferencesStore
│   ├── BaseHttpClient.cs           Shared HttpClient lifecycle, send + non-2xx logging + JSON helpers
│   ├── HarvestClient.cs            v2 timer start/stop + TestAsync
│   ├── TrelloClient.cs             v1 cards.create + TestAsync
│   ├── SlackClient.cs              chat.postMessage + TestAsync
│   └── IntegrationDispatcher.cs    Routes intents → clients with idempotency dedup; supports hot Reload
├── Pipeline/
│   ├── ITranscriber.cs             Implemented by WhisperTranscriber
│   ├── IIntentExtractor.cs         Implemented by LocalIntentExtractor (Phi-3.5)
│   ├── IIntegrationDispatcher.cs   Implemented by IntegrationDispatcher
│   └── LazyNativeModel.cs          Abstract base — init-once, serialized inference, drain-on-dispose
├── UI/
│   ├── MainWindow.cs               Layout shell + event-to-log translator
│   ├── FftPanel.cs                 NAudio FFT, log-spaced bands, 30 fps
│   ├── TranscriptLog.cs            Append-only log control: typed methods per event kind, internal UI marshalling, trim-on-overflow
│   └── SettingsDialog.cs           Tabbed prefs editor with inline "Test connection" probes
└── Tray/
    └── TrayIconFactory.cs          Procedurally drawn tray icons (no asset deps)
```

## How the audio path works

```
WasapiLoopbackCapture (IEEE float, device rate, stereo)
  → BufferedWaveProvider              (raw bytes from DataAvailable)
  → ToSampleProvider                  (interleaved float)
  → StereoToMono / MultiChannelToMono (downmix when needed)
  → WdlResamplingSampleProvider       (→ 16 kHz)
  → SpeechSegmenter                   (320-sample frames; energy VAD; segment buffer)
  → TranscribingAudioSink.PushAsync   (thread-pool hop)
  → WhisperTranscriber.TranscribeAsync (serialized native inference)
  → TranscriptionResult               (text + language; logged + Transcribed event)
  → LocalIntentExtractor.ExtractAsync (Phi-3.5 Mini, JSON output)
  → DetectedIntent[]                  (also surfaced via IntentsDetected event for the UI)
  → IntegrationDispatcher.DispatchAsync (Harvest / Trello / Slack with 60-s dedupe window)
```

A separate tap from `AudioCaptureService.Waveform` (a thread-safe ring buffer of
the most-recent ~512 ms of post-resample samples) feeds the FFT panel without
being on the hot audio path.

Reads happen synchronously inside `DataAvailable`; the only thread-pool hop is the
final segment dispatch, so the audio thread is never blocked by downstream work.

## Whisper bootstrap

On first run, `WhisperModelStore.EnsureAsync` downloads the configured ggml model
from `huggingface.co/ggerganov/whisper.cpp` into `%LOCALAPPDATA%\WorkIntel\models\`,
streamed with progress logging and atomically renamed into place. Subsequent runs
load instantly from disk.

The default is `base.en` (~140 MB, ~1× realtime on a modern CPU). To change models
or language, edit `WhisperOptions` in `Program.cs`:

```csharp
var whisperOptions = new WhisperOptions
{
    Model = WhisperModel.SmallEn,            // or TinyEn / MediumEn / LargeV3
    Language = "auto",                       // or "en", "de", …
    InitialPrompt = "WorkIntel, Harvest, Trello",  // bias toward in-domain vocab
    Translate = false,
};
```

GPU acceleration: replace the `Whisper.net.Runtime` package reference with
`Whisper.net.Runtime.Cuda` (CUDA) or `Whisper.net.Runtime.CoreML` (macOS).

## Intent extraction (local LLM)

Transcripts are handed off to **Phi-3.5 Mini Instruct** running locally via
LLamaSharp. The model produces a JSON array of typed intents, parsed by
`LocalIntentExtractor` and forwarded to the dispatcher. Nothing about the audio,
transcript, or extracted intents leaves the machine.

Model variants and approximate disk footprint:

| Variant                     | Quant   | Disk   | Quality vs. speed                |
|-----------------------------|---------|--------|----------------------------------|
| `Phi35MiniInstructQ4` (default) | Q4_K_M | ~2.4 GB | Fastest, good enough for intent  |
| `Phi35MiniInstructQ5`       | Q5_K_M  | ~2.8 GB | Marginally better at JSON output |
| `Phi35MiniInstructQ8`       | Q8_0    | ~4.0 GB | Near-lossless; slower             |

Configure via `LocalLlmOptions` in `Program.cs`:

```csharp
var llmOptions = new LocalLlmOptions
{
    Model = LocalLlmModel.Phi35MiniInstructQ4,
    ContextSize = 4096,
    Threads = Environment.ProcessorCount / 2,
    GpuLayerCount = 0,        // bump to 33 for full Phi-3.5 GPU offload
    Temperature = 0.1f,        // keep low for stable JSON
};
```

GPU offload: replace `LLamaSharp.Backend.Cpu` with `LLamaSharp.Backend.Cuda12` or
`LLamaSharp.Backend.Vulkan` and set `GpuLayerCount` (Phi-3.5 Mini has 33 layers
total).

The system prompt + few-shot examples live in `Intent/IntentSchema.cs`. Add a new
intent kind by: (1) adding a constant + one example to the prompt, (2) adding a
handler in the dispatcher.

### Current intent focus: Slack only

The system prompt is currently **scoped to Slack `chat.postMessage` only**.
Harvest and Trello dispatcher handlers remain in code (and their constants
in `IntentSchema`) so re-enabling them later is just a prompt edit, but the
LLM won't emit those intents in this iteration. The goal is to nail one
integration end-to-end before broadening surface area.

To re-enable an integration, edit the system prompt in
`Intent/IntentSchema.cs`: add the intent kind to the "Available intent
kinds" section, add one or two positive examples, and (importantly) keep
the existing negative examples so the model stays conservative.

## Setting up Slack

You need a Slack bot token (`xoxb-...`). One-time setup:

1. Open https://api.slack.com/apps → **Create New App** → **From scratch**.
   - Name: anything ("WorkIntel" works); pick your workspace.
2. In the app config, go to **OAuth & Permissions**.
3. Under **Scopes → Bot Token Scopes**, add `chat:write`. (Optionally also
   `chat:write.public` if you want the bot to post to channels it isn't
   explicitly a member of.)
4. Scroll up to **OAuth Tokens for Your Workspace** → **Install to Workspace**
   → review and authorize.
5. Copy the **Bot User OAuth Token** (starts with `xoxb-`).
6. In WorkIntel: tray → **Settings…** → **Slack** tab → paste into **Bot token**.
   Set **Default channel** to the channel you want as the fallback target
   when the user doesn't name one (e.g. `#general`).
7. Click **Test connection** — should report `✓ connected as <bot name> in <workspace>`.
8. Save. The dispatcher hot-reloads; no restart needed.
9. In Slack, add the bot to any channel you want it to post to:
   `#channel` → **integrations** → **Add an App** → pick your WorkIntel bot.
   (Skip this step if you added `chat:write.public` in step 3.)

### Verifying with the replay path

Record yourself saying something like *"Slack the team in #general that I'm
testing WorkIntel"* — Windows Voice Recorder app works fine, save as `.m4a`
or `.wav`. Then:

1. Tray → **Replay audio file…** → pick the recording.
2. Live monitor opens automatically.
3. You should see, in order:
   - The transcript line (cyan): `HH:MM:SS [en] Slack the team in #general that I'm testing WorkIntel`
   - The intent line (green): `→ slack.post_message  (conf 0.9X)  channel=#general, text=I'm testing WorkIntel`
   - The dispatch outcome (green ✓ if it landed, red ✗ if not):
     `✓ slack.post_message — slack message posted to C0123456`

If the dispatch fails, the message after the `✗` tells you what to fix:

| Outcome message | What to do |
|---|---|
| `slack not configured` | Bot token field is empty in Settings. |
| `Slack chat.postMessage failed: not_in_channel` | Add the bot to the target channel (Slack `#channel` → integrations). |
| `Slack chat.postMessage failed: channel_not_found` | Channel name doesn't match — try with/without the `#`, or use the channel ID. |
| `Slack chat.postMessage failed: invalid_auth` | Token rejected. Regenerate in Slack app config. |
| `Slack chat.postMessage failed: missing_scope` | Bot lacks `chat:write`. Add the scope in OAuth & Permissions, reinstall the app, copy the new token. |

### Tips

- The model is good at extracting the text *content* even when the speaker
  phrases the request awkwardly. "Hey, can you tell the eng channel that the
  build is green" → `channel: engineering` (without `#`, Slack still resolves
  it), `text: "The build is green"`.
- It's deliberately conservative on partial intents. "I should probably let
  the team know" → `[]` (no destination *and* no message text).
- The dispatcher dedupes intents within 60 s by `kind + sorted(params)` —
  saying the same thing twice in quick succession only posts once.

## Tray states

| State    | Icon  | Meaning                                                         |
|----------|-------|-----------------------------------------------------------------|
| Active   | green | Capture is running; speech detected within the idle window.     |
| Idle     | grey  | Capture is running; no speech for `IdleAfter` (default 45 s).   |
| Paused   | amber | User paused via the menu / tray double-click. Capture is gated. |

Double-clicking the tray icon opens the live monitor. Pause/resume is on the
right-click menu.

## Tray menu

- **Show live monitor…** (`Ctrl+Shift+W`) — opens the FFT + transcript window.
- **Pause / Resume capture** — toggles loopback capture.
- **Replay audio file…** — pick a WAV/MP3, watch the pipeline process it in
  real time. The single best smoke test for the whole stack.
- **Open log folder** — `%LOCALAPPDATA%\WorkIntel\` in Explorer.
- **Settings…** (`Ctrl+Shift+S`) — model / VAD / integration credentials.
- **Exit** — only this actually shuts down the app; closing the live monitor
  just hides it.

## Building

The repo now has two halves: a Windows desktop app and a containerized API/DB
backend. Build instructions per half.

### Backend (Postgres + API in Docker)

Requires Docker Desktop (Windows / Mac) or any docker engine + compose plugin.

```powershell
cd deploy
docker compose up -d --build
```

That starts:

- `workintel-postgres` on `127.0.0.1:5432`
- `workintel-api` (gRPC over HTTP/2 cleartext) on `127.0.0.1:5000`

Quick sanity checks:

```powershell
# Liveness probe (REST):
curl http://localhost:5000/health
# → {"status":"ok"}

# Drop into the database:
docker compose exec postgres psql -U workintel -d workintel -c "\dt"
# → should list the `tasks` table

# Stop everything:
docker compose down
# Stop everything AND wipe the database volume:
docker compose down -v
```

The API applies the schema on startup via EF Core's `EnsureCreatedAsync`. Once
the schema stabilises we'll switch to proper migrations (`dotnet ef migrations
add …`); for the POC, deleting the volume and restarting recreates the schema.

### Desktop app

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project src\WorkIntel\WorkIntel.csproj
```

Logs land in `%LOCALAPPDATA%\WorkIntel\workintel.log`.

The desktop app will (after the Phase 2 wire-up) connect to the API at
`http://localhost:5000` by default. Connection string lives in the Settings
dialog → Backend tab (not yet wired in this commit).

## First run — what to expect

The first time you launch WorkIntel, a tray icon appears immediately but the
intelligence isn't ready yet. **Two model files have to download** before
transcription and intent extraction work:

- `ggml-base.en.bin` (~140 MB) — Whisper, English-only base model.
- `Phi-3.5-mini-instruct-Q4_K_M.gguf` (~2.4 GB) — Phi-3.5 Mini, 4-bit quant.

Both pull from Hugging Face on first launch and cache to
`%LOCALAPPDATA%\WorkIntel\models\`. Subsequent launches start instantly.

To watch the download progress: tray → "Open log folder" → `workintel.log`,
or `Get-Content -Wait workintel.log` in PowerShell. You'll see lines like:

```
2026-05-08 14:02:11.345 [INFO ] [  3] downloading whisper model: ggml-base.en.bin from huggingface.co/...
2026-05-08 14:02:13.012 [INFO ] [  4] whisper model download: 23 / 142 MB (16.2%)
2026-05-08 14:02:18.880 [INFO ] [  3] whisper processor ready
2026-05-08 14:02:19.110 [INFO ] [  5] downloading local LLM: Phi-3.5-mini-instruct-Q4_K_M.gguf from huggingface.co/...
```

Behind a corporate proxy or with HF blocked? Drop the files into the models
folder yourself; the loaders treat any file >100 MB at the expected name as
cached and skip the download.

## Verifying it works (replay path)

You can verify the full pipeline end-to-end without saying anything out loud or
playing media through your system: tray → **"Replay audio file…"** → pick a WAV
or MP3. The file streams through the same audio path that WASAPI loopback
feeds, in real time. Live capture is suppressed for the duration so the
display stays clean.

What you should see in the live monitor:

1. The FFT bars respond to whatever audio is playing.
2. As Whisper closes each VAD-bounded segment (a sentence or two of speech),
   a transcript line appears in cyan, prefixed with `HH:MM:SS [language]`.
3. As Phi-3.5 finds an intent, a green `→ kind  (conf 0.92)  key=value, …`
   line appears below the transcript.
4. State transitions (Active → Idle → Active) appear as muted italic lines.

If the file is purely instrumental music or background noise, you should see
the FFT respond but no transcripts (the VAD discards sub-min energy and
Whisper produces empty output for non-speech). That's the expected behaviour;
to verify transcription you need a clip with at least a few seconds of
intelligible speech.

The simplest local test material is a short voice memo recorded with the
Windows Voice Recorder app, exported as M4A or WAV.

## Verifying it works (live path)

Once the models are loaded:

1. Open the live monitor (tray double-click, or "Show live monitor…").
2. Play any audio with speech — a YouTube video, a podcast, a video call.
3. Watch the FFT and the log.

Recommended for the first end-to-end sanity check: read this sentence aloud
into a video call you're hosting, with WorkIntel running. You should see your
own audio's FFT and a transcript of the sentence.

## Testing

```powershell
dotnet test
```

Two test projects:

- `tests\WorkIntel.Tests\` — desktop pipeline units (VAD, segmenter, ring
  buffer, intent JSON parser, idempotency, preferences, state machine,
  lifecycle base, HTTP client base, log control).
- `tests\WorkIntel.Api.Tests\` — gRPC API tests against a real Postgres in a
  Testcontainers-managed container. Each run pulls (or reuses cached)
  `postgres:16-alpine`, boots a fresh DB, applies the schema, exercises the
  full CRUD path through the gRPC client. **Requires Docker to be running on
  the test host** (the Testcontainers prerequisite, not WorkIntel-specific).

No external mocking framework. No fixture audio files — anything that touches
a real Whisper model, LLamaSharp weights, or live external APIs is left out of
the unit-test surface and tested manually via the replay path. Coverage focuses on the gnarly pure-logic units where
regressions hurt:

- `EnergyVadTests` — activation/deactivation thresholds, hangover, max-segment force-close, reset.
- `SpeechSegmenterTests` — silence, full speech-then-silence emission cycle, sub-min discard, reset.
- `LocalIntentExtractorTests` — JSON parsing robustness (markdown fences, trailing prose, malformed input, brackets in strings, the `none` filter).
- `WaveformRingBufferTests` — under-fill, exact-fill, over-fill, wrap-around, multiple writes.
- `IntegrationDispatcherTests` — `ComputeKey` stability + `IsDuplicate` window behavior.
- `StateManagerTests` — Idle ↔ Active ↔ Paused transitions, no-op detection, paused-ignores-speech.
- `PreferencesStoreTests` — DPAPI round-trip, legacy `config.json` migration, secrets aren't visible in the on-disk wrapper, atomic overwrite.
- `LazyNativeModelTests` — lifecycle contract for the model-loading base class: warmup idempotency, race-free init under 16-way concurrent first-touch, failure caching, dispose drain + timeout, idempotent dispose, post-dispose `EnsureInitializedAsync` throws.
- `BaseHttpClientTests` — driven by a stub `HttpMessageHandler`: path resolution against the base URL, user-agent passthrough, JSON GET/POST round-trip, content-type header, non-2xx → `HttpRequestException` with status in the message, transport failure passthrough, body truncation, cancellation propagation.

> **Windows-only.** `PreferencesStoreTests` exercises DPAPI; the project itself
> is Windows-only so this is fine, but expect those tests to fail on Linux/macOS
> CI.

Things deliberately *not* covered yet (because they need real models or live
network and live in the "integration" category for a future test run):

- End-to-end Whisper inference against a fixture WAV.
- End-to-end Phi-3.5 prompting.
- `HarvestClient` / `TrelloClient` / `SlackClient` — these are thin enough that
  contract tests against a `WireMock`-style stub are higher-leverage than
  unit-mocking them, but neither exists yet.

## Live monitor window

Tray → "Show live monitor…" (or `Ctrl+Shift+W`, or double-click the tray icon)
opens an inspector window with two panes:

- **FFT spectrum** — log-frequency bars from 80 Hz to ~7.8 kHz, peak-hold smoothed,
  ~30 fps. Polls `WaveformRingBuffer` on a UI timer; no event coupling.
- **Live log** — scrolling color-coded feed of every transcript and every detected
  intent (with parameters). Self-trims to ~200 KB to stay snappy on long sessions.

Closing the window only hides it; only the tray "Exit" item shuts the app down.

## Settings & credentials

Tray → "Settings…" (or `Ctrl+Shift+S`) opens a tabbed editor:

- **Capture** — idle threshold, VAD activation/deactivation thresholds, hangover.
- **Whisper** — model, language, initial prompt, translate, threads.
- **Phi-3.5 LLM** — model variant, context size, threads, GPU offload, temperature.
- **Harvest / Trello / Slack** — credentials + each tab has an inline "Test
  connection" button that probes the live API and reports the connected identity.

On Save, hot-reloadable preferences (capture thresholds, integration credentials)
apply immediately — `IntegrationDispatcher.Reload(...)` swaps the underlying
clients with a 5 s grace period for in-flight requests. Whisper / LLM model
changes prompt for restart.

Storage: `%LOCALAPPDATA%\WorkIntel\preferences.dat`. The outer JSON wrapper is
plaintext (schema version + save timestamp) so you can confirm "what's stored"
without DPAPI; the inner blob is encrypted with `ProtectedData.Protect` scoped
to the current user with app-specific entropy. The blob is therefore
non-portable: copy it to another machine or another user account and it won't
decrypt.

Legacy `config.json` (plaintext, pre-Phase-4) is migrated automatically on first
launch and renamed to `config.json.migrated.bak`.

Env-var overlay still works for headless / CI setups (any blank secret field is
filled from `WORKINTEL_*` env vars at load time, but env values aren't persisted):

| Field                            | Env var fallback                         |
|----------------------------------|------------------------------------------|
| `harvest.accountId`              | `WORKINTEL_HARVEST_ACCOUNT_ID`           |
| `harvest.accessToken`            | `WORKINTEL_HARVEST_TOKEN`                |
| `harvest.defaultProjectId`       | `WORKINTEL_HARVEST_DEFAULT_PROJECT_ID`   |
| `harvest.defaultTaskId`          | `WORKINTEL_HARVEST_DEFAULT_TASK_ID`      |
| `trello.apiKey`                  | `WORKINTEL_TRELLO_KEY`                   |
| `trello.token`                   | `WORKINTEL_TRELLO_TOKEN`                 |
| `trello.defaultListId`           | `WORKINTEL_TRELLO_DEFAULT_LIST`          |
| `slack.botToken`                 | `WORKINTEL_SLACK_BOT_TOKEN`              |
| `slack.defaultChannel`           | `WORKINTEL_SLACK_DEFAULT_CHANNEL`        |

`IntegrationDispatcher` deduplicates intents within a 60 s sliding window using a
SHA-1 hash of `kind + sorted(params)` so the same intent emitted across two
adjacent Whisper segments doesn't double-fire.

## Roadmap

We're currently in the engineering loop: **prototype → test → refactor → release.**

1. ~~**Phase 1 — Audio capture + tray scaffold**~~ done.
2. ~~**Phase 2 — Whisper.net sink**~~ done.
3. ~~**Phase 3 — Local intent extraction (Phi-3.5)**~~ done.
4. ~~**Phase 4 — Integration dispatchers (Harvest / Trello / Slack)**~~ done.
5. ~~**Phase 5 — Settings UI + DPAPI secret storage**~~ done.
6. ~~**Phase 6 — Unit tests (current)**~~ done. xUnit harness covering the
   pure-logic units; integration / model-loading tests deferred.
7. **Phase 7 — Refactor pass.** Three refactors done:
   - `LazyNativeModel` extracted as a shared base for `WhisperTranscriber` and
     `LocalIntentExtractor`. Lifecycle (init lock, process lock, init-failed
     flag, drain-on-dispose) now lives in one tested place; subclasses only
     declare `LoadCoreAsync` / `ReleaseResourcesAsync` and the inference body.
   - `BaseHttpClient` extracted as a shared base for `HarvestClient`,
     `TrelloClient`, `SlackClient`. Owns the `HttpClient` lifetime, send +
     non-2xx logging + body truncation, and JSON GET/POST helpers. Each
     subclass shrank to ~60 lines of just-the-domain logic.
   - `TranscriptLog` extracted from `MainWindow`. Owns the `RichTextBox`,
     color palette, formatting policy, trim-on-overflow, and internal UI-thread
     marshalling. `MainWindow` is now strictly a layout shell + event
     translator. Adding a new visible event kind (e.g. dispatch outcome)
     becomes one method on the log + one subscription on the form.
8. **Phase 8 — Release prep.** Single-file publish, code-signed exe, Inno Setup
   installer, version metadata in the About dialog, telemetry-free crash dumps.
9. **Backlog (post-release):**
   - **Better VAD** — Silero VAD (ONNX runtime) in place of the energy detector.
   - **Trello list-name resolution** — board-list cache so the LLM can target
     a list by name instead of an ID.
   - **Dispatcher → UI feedback** — surface dispatch outcome next to each intent
     line in the live monitor.
   - **Persist idempotency cache across restarts** — currently in-memory only.
   - **Smoke-test harness** — replay a fixture WAV through the full pipeline
     for offline regression testing of the model-touching paths.
   - **Pluggable inference backend** — `IUnifiedAudioBackend` abstraction so a
     containerized audio LLM (MOSS-Audio, Voxtral, Qwen-Audio) can opt in
     alongside the local Whisper + Phi-3.5 default.

## Privacy posture

| Component              | Network calls                                                        |
|------------------------|----------------------------------------------------------------------|
| WASAPI capture         | None                                                                 |
| Whisper transcription  | None at runtime (one-time HF download of public weights on first use)|
| Phi-3.5 intent extract | None at runtime (one-time HF download of public weights on first use)|
| Harvest / Trello / Slack dispatch | Only to the user-configured integration endpoints, only on a detected intent (Phase 3) |

Audio bytes, transcripts, and detected intents never leave WorkIntel except as
explicit, user-authorized API calls to the user's own Harvest / Trello / Slack
workspace.

## License

TBD (private project).
