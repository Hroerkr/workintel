# WorkIntel

A personal work-intelligence stack. Two input streams — system audio (live
loopback transcription) and Slack DMs — feed a local LLM that extracts task
candidates, which land in a Postgres database via a containerized gRPC API.
A triage UI lets you Include / Exclude / Remove each task; Included tasks
can be exported by email to a Trello / Jira board.

> **Status (in progress).** Working today: desktop tray app, WASAPI loopback
> capture, Whisper transcription, local Phi-3.5 LLM extracting structured JSON
> from each transcript, FFT + live transcript monitor, DPAPI-protected
> settings dialog, WAV replay path for deterministic testing. The backend
> (Postgres + gRPC service in Docker) is built and tested in isolation but
> the desktop client hasn't been wired to it yet. Slack DM listener and
> email export are next.

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

Design choices, with the reasoning:

- **Postgres over SQLite** — data outlives desktop reinstalls, multi-user is
  a config flip not a schema migration, standard admin tooling works
  (pgAdmin, DBeaver, `psql`).
- **gRPC for the API surface** — server-side streaming for live task updates
  in the UI without polling, schema-as-code via `.proto`, generated client
  code for free. REST surface via gRPC-JSON transcoding is on the backlog
  for `curl`-friendly debugging.
- **Email export instead of per-product API integrations** — Trello and Jira
  both support "email to board" natively. One outbound mechanism, no
  per-vendor auth ceremony or rate-limit handling.
- **Local LLM** — all transcript and message content stays on the device.
  After first-run model downloads, the only outbound calls are the Slack
  Socket Mode WebSocket (for DM events) and the email export.

## Stack

- **.NET 8 / WinForms** desktop, **ASP.NET Core 8 gRPC** service, **Postgres 16**.
- **NAudio 2.2** — WASAPI loopback capture + WDL resampler.
- **Whisper.net 1.5** — local speech-to-text (Whisper.cpp under the hood).
- **LLamaSharp 0.20** — local LLM inference (llama.cpp under the hood) running
  **Phi-3.5 Mini Instruct** (Q4_K_M by default).
- **EF Core 8 + Npgsql** for the data layer; **Grpc.AspNetCore** for the service.
- **xUnit + Testcontainers** for the API test surface (real Postgres per test
  collection).

## Project layout

```
WorkIntel/
├── README.md                          this file
├── WorkIntel.sln
├── .gitignore   .dockerignore
├── deploy/
│   └── docker-compose.yml             Postgres + API
├── src/
│   ├── WorkIntel/                     Windows desktop tray app
│   ├── WorkIntel.Contracts/           .proto + generated gRPC client/server stubs
│   ├── WorkIntel.Data/                EF Core entity + DbContext
│   └── WorkIntel.Api/                 ASP.NET Core gRPC service (Docker)
└── tests/
    ├── WorkIntel.Tests/               Desktop pipeline unit tests (xUnit)
    └── WorkIntel.Api.Tests/           gRPC service tests against Testcontainers Postgres
```

The desktop project breaks down further:

```
src/WorkIntel/
├── Program.cs                         Entry + single-instance guard
├── TrayApplicationContext.cs          NotifyIcon, menu, state ↔ UI marshalling
├── App/                               State machine, AppState enum, logging
├── Audio/
│   ├── AudioCaptureService.cs         WASAPI loopback → mono → 16 kHz
│   ├── EnergyVad.cs                   Two-threshold + hangover VAD
│   ├── SpeechSegmenter.cs             Frames audio, emits SpeechSegment per utterance
│   ├── WaveformRingBuffer.cs          Lock-protected ring for the FFT tap
│   └── …
├── Transcription/
│   ├── WhisperOptions.cs              Model, language, threads, prompt
│   ├── WhisperModelStore.cs           Locate / download / atomically install ggml
│   ├── WhisperTranscriber.cs          Inherits LazyNativeModel; serialized inference
│   └── TranscribingAudioSink.cs       SpeechSegment → transcript → LLM → events
├── Intent/
│   ├── LocalLlmOptions.cs             Model variant, ctx size, threads, gpu, sampling
│   ├── LocalLlmModelStore.cs          Locate / download / atomically install gguf
│   ├── IntentSchema.cs                JSON schema + few-shot system prompt
│   └── LocalIntentExtractor.cs        Inherits LazyNativeModel; runs the prompt
├── Configuration/
│   ├── DpapiVault.cs                  ProtectedData.Protect/Unprotect (user-scoped)
│   ├── AppPreferences.cs              Root preferences record
│   └── PreferencesStore.cs            DPAPI-encrypted save/load + legacy migration
├── Pipeline/
│   ├── ITranscriber.cs                Whisper plug-point
│   ├── IIntentExtractor.cs            LLM plug-point
│   └── LazyNativeModel.cs             Abstract base: lazy init, serialized inference, drain-on-dispose
├── UI/
│   ├── MainWindow.cs                  Live monitor: FFT + transcript log
│   ├── FftPanel.cs                    Log-spaced FFT bars, 30 fps
│   ├── TranscriptLog.cs               Append-only color-coded event log
│   └── SettingsDialog.cs              Tabbed preferences editor
├── Tray/
│   └── TrayIconFactory.cs             Procedurally drawn tray icons
└── Integrations/                      [vestigial — pending removal]
    ├── BaseHttpClient.cs              Shared HTTP plumbing
    ├── Harvest/Trello/SlackClient.cs  Outbound API clients from earlier design
    └── IntegrationDispatcher.cs       Routes intents → clients
```

The `Integrations/` folder is leftover from an earlier outbound-dispatch design.
It still compiles and tests still pass, but the LLM no longer emits intents
that drive it. Pending removal once the new task-store path is wired through
the desktop.

## How the audio path works

```
WasapiLoopbackCapture (IEEE float, device rate, stereo)
  → BufferedWaveProvider              (raw bytes from DataAvailable)
  → ToSampleProvider                  (interleaved float)
  → StereoToMono / MultiChannelToMono (downmix when needed)
  → WdlResamplingSampleProvider       (→ 16 kHz mono)
  → SpeechSegmenter                   (320-sample frames; energy VAD; segment buffer)
  → TranscribingAudioSink.PushAsync   (thread-pool hop)
  → WhisperTranscriber.TranscribeAsync (serialized native inference)
  → TranscriptionResult               (text + language; surfaced via Transcribed event)
  → LocalIntentExtractor.ExtractAsync (Phi-3.5 Mini; JSON output)
  → DetectedIntent[]                  (surfaced via IntentsDetected event for the UI)
```

A separate tap from `AudioCaptureService.Waveform` (a thread-safe ring buffer
of the most-recent ~512 ms of post-resample samples) feeds the FFT panel
without sitting on the hot audio path.

Reads happen synchronously inside `DataAvailable`; the only thread-pool hop
is the final segment dispatch, so the audio thread is never blocked by
downstream work.

## Whisper bootstrap

On first run, `WhisperModelStore.EnsureAsync` downloads the configured ggml
model from `huggingface.co/ggerganov/whisper.cpp` into
`%LOCALAPPDATA%\WorkIntel\models\`, streamed with progress logging and
atomically renamed into place. Subsequent runs load instantly from disk.

The default is `base.en` (~140 MB, ~1× realtime on a modern CPU). To change
models or language, edit `WhisperOptions` in `Program.cs`:

```csharp
var whisperOptions = new WhisperOptions
{
    Model = WhisperModel.SmallEn,            // or TinyEn / MediumEn / LargeV3
    Language = "auto",                       // or "en", "de", …
    InitialPrompt = "WorkIntel, Phi, Whisper", // bias toward in-domain vocab
    Translate = false,
};
```

GPU acceleration: swap `Whisper.net.Runtime` for `Whisper.net.Runtime.Cuda`
(CUDA) or `Whisper.net.Runtime.CoreML` (macOS).

## Local LLM (Phi-3.5)

Transcripts are handed off to **Phi-3.5 Mini Instruct** running locally via
LLamaSharp. The model produces a JSON array conforming to the schema in
`Intent/IntentSchema.cs`. Nothing about the audio, transcript, or LLM output
leaves the machine.

Model variants and approximate disk footprint:

| Variant                          | Quant   | Disk    | Notes                              |
|----------------------------------|---------|---------|------------------------------------|
| `Phi35MiniInstructQ4` (default)  | Q4_K_M  | ~2.4 GB | Fastest, good enough for the task  |
| `Phi35MiniInstructQ5`            | Q5_K_M  | ~2.8 GB | Marginally better at JSON output   |
| `Phi35MiniInstructQ8`            | Q8_0    | ~4.0 GB | Near-lossless; slower              |

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

GPU offload: replace `LLamaSharp.Backend.Cpu` with `LLamaSharp.Backend.Cuda12`
or `LLamaSharp.Backend.Vulkan` and set `GpuLayerCount` (Phi-3.5 Mini has 33
layers total).

The system prompt + few-shot examples live in `Intent/IntentSchema.cs`. The
prompt is in transition — the current shape is geared toward the (now
deprecated) outbound-dispatch model. It's being rewritten into a
"task candidate" schema (`title`, `description`, `owner`, `deadline`,
`confidence`, `source_quote`) as part of wiring the desktop into the new
backend.

## Building

The repo has two halves: a Windows desktop app and a containerized API + DB
backend. Build instructions per half.

### Backend (Postgres + API in Docker)

Requires Docker Desktop (Windows / Mac) or any Docker engine + Compose plugin.

```powershell
cd deploy
docker compose up -d --build
```

That starts:

- `workintel-postgres` on `127.0.0.1:5432`
- `workintel-api` (gRPC over HTTP/2 cleartext) on `127.0.0.1:5000`

Quick sanity checks:

```powershell
# Liveness probe (REST)
curl http://localhost:5000/health
# → {"status":"ok"}

# Drop into the database
docker compose exec postgres psql -U workintel -d workintel -c "\dt"
# → should list the `tasks` table

# Stop everything
docker compose down
# Stop everything AND wipe the database volume
docker compose down -v
```

The API applies the schema on startup via EF Core's `EnsureCreatedAsync`.
Once the schema stabilises we'll switch to proper migrations
(`dotnet ef migrations add …`); for the POC, deleting the volume and
restarting recreates the schema.

### Desktop app

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project src\WorkIntel\WorkIntel.csproj
```

Logs land in `%LOCALAPPDATA%\WorkIntel\workintel.log`.

The desktop app currently runs the in-process pipeline only. Wiring it to
the gRPC backend is the next phase — it'll connect to
`http://localhost:5000` by default with the endpoint and credentials stored
in the DPAPI-protected preferences blob.

## First run — what to expect

The first time you launch WorkIntel, a tray icon appears immediately but
the intelligence isn't ready yet. **Two model files have to download** before
transcription and LLM extraction work:

- `ggml-base.en.bin` (~140 MB) — Whisper, English-only base model.
- `Phi-3.5-mini-instruct-Q4_K_M.gguf` (~2.4 GB) — Phi-3.5 Mini, 4-bit quant.

Both pull from Hugging Face on first launch and cache to
`%LOCALAPPDATA%\WorkIntel\models\`. Subsequent launches start instantly.

To watch the download progress: tray → "Open log folder" → `workintel.log`,
or `Get-Content -Wait workintel.log` in PowerShell. You'll see lines like:

```
… [INFO ] downloading whisper model: ggml-base.en.bin from huggingface.co/…
… [INFO ] whisper model download: 23 / 142 MB (16.2%)
… [INFO ] whisper processor ready
… [INFO ] downloading local LLM: Phi-3.5-mini-instruct-Q4_K_M.gguf from huggingface.co/…
```

Behind a corporate proxy or with HF blocked? Drop the files into the models
folder yourself; the loaders treat any file >100 MB at the expected name as
cached and skip the download.

## Verifying it works (replay path)

You can drive the full pipeline end-to-end without saying anything out
loud or playing media through your system: tray → **"Replay audio file…"**
→ pick a WAV or MP3. The file streams through the same audio path WASAPI
loopback feeds, in real time. Live capture is suppressed for the duration
so the display stays clean.

What you should see in the live monitor:

1. The FFT bars respond to whatever audio is playing.
2. As Whisper closes each VAD-bounded segment (a sentence or two of
   speech), a transcript line appears in cyan, prefixed with
   `HH:MM:SS [language]`.
3. If the LLM extracts anything from the transcript, a green
   `→ kind  (conf 0.92)  key=value, …` line appears below.
4. State transitions (Active ↔ Idle) appear as muted italic lines.

If the file is purely instrumental music or background noise, you should
see the FFT respond but no transcripts — VAD discards sub-min energy and
Whisper produces empty output for non-speech.

The simplest local test material is a short voice memo recorded with the
Windows Voice Recorder app, exported as M4A or WAV.

## Verifying it works (live path)

Once the models are loaded:

1. Open the live monitor (tray double-click, or "Show live monitor…").
2. Play any audio with speech — a YouTube video, a podcast, a video call.
3. Watch the FFT and the log.

## Tray states

| State    | Icon  | Meaning                                                         |
|----------|-------|-----------------------------------------------------------------|
| Active   | green | Capture is running; speech detected within the idle window.     |
| Idle     | grey  | Capture is running; no speech for `IdleAfter` (default 45 s).   |
| Paused   | amber | User paused via the menu / tray double-click. Capture is gated. |

Double-clicking the tray icon opens the live monitor. Pause/resume is on
the right-click menu.

## Tray menu

- **Show live monitor…** (`Ctrl+Shift+W`) — opens the FFT + transcript window.
- **Pause / Resume capture** — toggles loopback capture.
- **Replay audio file…** — pick a WAV/MP3, watch the pipeline process it in
  real time. The single best smoke test for the whole stack.
- **Open log folder** — `%LOCALAPPDATA%\WorkIntel\` in Explorer.
- **Settings…** (`Ctrl+Shift+S`) — model / VAD / preferences.
- **Exit** — only this actually shuts the app down; closing the live
  monitor just hides it.

## Live monitor window

Tray → "Show live monitor…" (or `Ctrl+Shift+W`, or double-click the tray
icon) opens an inspector window with two panes:

- **FFT spectrum** — log-frequency bars from 80 Hz to ~7.8 kHz, peak-hold
  smoothed, ~30 fps. Polls `WaveformRingBuffer` on a UI timer; no event
  coupling.
- **Live log** — scrolling color-coded feed of every transcript and every
  LLM output. Self-trims to ~200 KB to stay snappy on long sessions.

Closing the window only hides it; only the tray "Exit" item shuts the
app down.

## Settings & credentials

Tray → "Settings…" (or `Ctrl+Shift+S`) opens a tabbed editor:

- **Capture** — idle threshold, VAD activation / deactivation thresholds,
  hangover.
- **Whisper** — model, language, initial prompt, translate flag, threads.
- **Phi-3.5 LLM** — model variant, context size, threads, GPU offload,
  temperature.

A **Backend** tab will be added when the desktop wires up to the gRPC
service — it'll hold the API endpoint URL and (eventually) an
auth token, both stored alongside everything else in the DPAPI-encrypted
preferences blob.

Storage: `%LOCALAPPDATA%\WorkIntel\preferences.dat`. The outer JSON wrapper
is plaintext (schema version + save timestamp) so you can confirm
"what's stored" without DPAPI; the inner blob is encrypted with
`ProtectedData.Protect` scoped to the current user with app-specific
entropy. The blob is therefore non-portable across machines or user
accounts.

## Testing

```powershell
dotnet test
```

Two test projects:

- `tests\WorkIntel.Tests\` — desktop pipeline units (VAD, segmenter, ring
  buffer, JSON parser, idempotency, preferences, state machine, lifecycle
  base, HTTP client base, log control). 77 tests, ~1.5 s wall.
- `tests\WorkIntel.Api.Tests\` — gRPC API tests against a real Postgres in
  a Testcontainers-managed container. Each run pulls (or reuses cached)
  `postgres:16-alpine`, boots a fresh DB, applies the schema, exercises
  the full CRUD path through the gRPC client. **Requires Docker to be
  running on the test host** (Testcontainers prerequisite, not
  WorkIntel-specific).

Coverage focuses on the pure-logic units where regressions hurt:

- `EnergyVadTests` — activation/deactivation thresholds, hangover,
  max-segment force-close, reset.
- `SpeechSegmenterTests` — silence, full speech-then-silence emission
  cycle, sub-min discard, reset.
- `LocalIntentExtractorTests` — JSON parsing robustness (markdown fences,
  trailing prose, malformed input, brackets in strings, the `none` filter).
- `WaveformRingBufferTests` — under-fill, exact-fill, over-fill,
  wrap-around, multiple writes.
- `StateManagerTests` — Idle ↔ Active ↔ Paused transitions, no-op
  detection, paused-ignores-speech.
- `PreferencesStoreTests` — DPAPI round-trip, legacy `config.json`
  migration, secrets aren't visible in the on-disk wrapper, atomic
  overwrite.
- `LazyNativeModelTests` — lifecycle contract for the model-loading base
  class: warmup idempotency, race-free init under 16-way concurrent
  first-touch, failure caching, dispose drain + timeout, idempotent
  dispose, post-dispose `EnsureInitializedAsync` throws.
- `BaseHttpClientTests` — stub `HttpMessageHandler`: path resolution
  against the base URL, user-agent passthrough, JSON GET/POST round-trip,
  content-type header, non-2xx → `HttpRequestException` with status in
  the message, transport failure passthrough, body truncation,
  cancellation propagation.
- `TaskServiceTests` (API) — end-to-end gRPC CRUD round-trip: create + get,
  invalid id → `InvalidArgument`, not found → `NotFound`, list ordering
  by `detected_at DESC`, status filter, `exported_at` auto-stamp on the
  exported transition, double-delete idempotency.

> **Windows-only desktop tests.** `PreferencesStoreTests` exercises DPAPI;
> the desktop project itself is Windows-only so this is fine, but those
> tests will fail on Linux/macOS CI. The API tests target net8.0 (no
> WinForms dependency) and run anywhere Docker is available.

Things deliberately *not* covered yet:

- End-to-end Whisper inference against a fixture WAV (needs the real model;
  exercised manually via the replay path).
- End-to-end Phi-3.5 prompting (same reason).
- `IntegrationDispatcher` legacy paths — vestigial code, pending removal.

## Roadmap

1. ✅ **Phase 1 — Audio capture + tray scaffold.**
2. ✅ **Phase 2 — Whisper transcription.**
3. ✅ **Phase 3 — Local LLM extraction (Phi-3.5).**
4. ✅ **Phase 4 — Settings UI + DPAPI secret storage.**
5. ✅ **Phase 5 — Live monitor window (FFT + transcript log).**
6. ✅ **Phase 6 — Unit tests + refactor pass.** `LazyNativeModel`,
   `BaseHttpClient`, and `TranscriptLog` extracted; 77 tests green.
7. ✅ **Phase 7 — Containerized backend.** Postgres + ASP.NET Core gRPC
   service, EF Core schema, Testcontainers-driven integration tests.
8. **Phase 8 — Desktop ↔ backend wire-up (next).** Generated gRPC client
   referenced from the desktop, `RemoteTaskStore` replacing the dispatcher
   hop, new Tasks tab in the live monitor populated by `ListTasks` +
   `StreamTaskEvents`. Reframe the LLM prompt from intent-style to
   task-candidate-style as part of this.
9. **Phase 9 — Slack DM listener.** SlackNet over Socket Mode, user-scope
   `im:history`, subscribed to `message.im`. Each incoming DM goes through
   the same task-extraction prompt and lands in the same task store.
10. **Phase 10 — Triage UI.** Tasks tab with sortable list, status filters
    (Pending / Included / Excluded / Exported), Include / Exclude / Remove
    actions.
11. **Phase 11 — Email export.** SMTP config + named destinations
    (Trello-board email, Jira-project email). Included tasks get sent
    with subject = title, body = original context + extracted summary;
    status flips to `exported` with the destination recorded.
12. **Phase 12 — Release prep.** Single-file publish, code-signed exe,
    Inno Setup installer, version metadata in the About dialog,
    telemetry-free crash dumps.
13. **Backlog (post-release):**
    - **Silero VAD** (ONNX runtime) in place of the energy detector.
    - **REST surface via gRPC-JSON transcoding** for `curl`-friendly debugging.
    - **EF Core migrations** in place of `EnsureCreatedAsync`.
    - **Bearer-token auth** on the API (gates any remote deployment).
    - **Multi-user** — schema already has `user_id`; needs auth + UI scoping.

## Privacy posture

| Component                   | Network calls                                                              |
|-----------------------------|----------------------------------------------------------------------------|
| WASAPI loopback capture     | None.                                                                      |
| Whisper transcription       | None at runtime. One-time HF download of public weights on first use.      |
| Phi-3.5 extraction          | None at runtime. One-time HF download of public weights on first use.      |
| Desktop ↔ API (gRPC)        | Local loopback only (localhost:5000) until a remote-deploy phase.          |
| Slack DM listener (planned) | Persistent WebSocket to slack.com for `message.im` events.                 |
| Email export (planned)      | SMTP to your configured email account, sending to board-ingest addresses.  |

Audio bytes, transcripts, and LLM output stay on the device. The only
runtime outbound traffic, once Phase 9 and 11 land, is the Slack event
stream and the email export — both to endpoints you configure.

## License

TBD (private project).
