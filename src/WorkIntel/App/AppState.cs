namespace WorkIntel.App;

/// <summary>
/// High-level state surface that drives the tray icon and capture behavior.
/// </summary>
public enum AppState
{
    /// <summary>Capture is running and the VAD has detected recent speech.</summary>
    Active,

    /// <summary>Capture is running but no speech has been detected for a while.</summary>
    Idle,

    /// <summary>User explicitly paused capture; pipeline is dormant.</summary>
    Paused
}
