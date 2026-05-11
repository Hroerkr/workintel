using WorkIntel.Integrations;
using WorkIntel.Intent;
using WorkIntel.Transcription;

namespace WorkIntel.Configuration;

/// <summary>
/// Root preferences object — everything the user can configure through the
/// settings dialog. Persisted by <see cref="PreferencesStore"/>; the entire
/// blob is DPAPI-encrypted on disk.
/// </summary>
public sealed class AppPreferences
{
    /// <summary>Bumped whenever the schema changes; lets future loaders migrate forward.</summary>
    public string SchemaVersion { get; set; } = "1";

    public CapturePreferences Capture { get; set; } = new();
    public WhisperOptions Whisper { get; set; } = new();
    public LocalLlmOptions Llm { get; set; } = new();
    public IntegrationSecrets Secrets { get; set; } = new();
}

/// <summary>
/// Capture-pipeline tuning knobs (idle threshold, VAD parameters). Separate from
/// <see cref="WorkIntel.Audio.EnergyVad"/> so the user-visible config has a
/// stable shape independent of the underlying VAD impl.
/// </summary>
public sealed class CapturePreferences
{
    /// <summary>Seconds without speech before Active → Idle.</summary>
    public int IdleAfterSeconds { get; set; } = 45;

    /// <summary>RMS dBFS above which the VAD opens a speech segment.</summary>
    public float VadActivationDb { get; set; } = -42f;

    /// <summary>RMS dBFS below which the VAD lets a segment close (with hangover).</summary>
    public float VadDeactivationDb { get; set; } = -50f;

    /// <summary>How long after the last loud frame the VAD keeps a segment open.</summary>
    public int VadHangoverMs { get; set; } = 700;
}
