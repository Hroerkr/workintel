using System;
using System.Threading;

namespace WorkIntel.App;

/// <summary>
/// Owns the active/idle/paused state machine. Inputs come from the audio pipeline
/// (NotifySpeechActivity / NotifySilence) and the user (TogglePause). Consumers
/// subscribe to <see cref="StateChanged"/> for UI updates.
/// </summary>
/// <remarks>
/// <para>
/// Idle is auto-derived: if no speech has been observed for <see cref="IdleAfter"/>,
/// the state moves Active → Idle. A speech notification flips it back to Active.
/// </para>
/// <para>
/// Paused is sticky and only the user can flip it. While paused, speech notifications
/// are ignored (the audio service should also stop pumping samples).
/// </para>
/// </remarks>
public sealed class StateManager : IDisposable
{
    /// <summary>How long without speech before we drop to <see cref="AppState.Idle"/>.
    /// Hot-mutable; the next idle-evaluation tick picks up the new value.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromSeconds(45);

    private readonly object _lock = new();
    private AppState _current = AppState.Idle;
    private DateTimeOffset _lastSpeechAt = DateTimeOffset.MinValue;
    // Fully-qualified — both System.Threading.Timer and System.Windows.Forms.Timer
    // are in scope under WinForms + ImplicitUsings. We want the threading one;
    // it ticks on the thread pool, independent of the UI message loop.
    private readonly System.Threading.Timer _idleTimer;

    public AppState Current
    {
        get { lock (_lock) return _current; }
    }

    public DateTimeOffset LastSpeechAt
    {
        get { lock (_lock) return _lastSpeechAt; }
    }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public StateManager()
    {
        // Periodic check: cheaper and simpler than scheduling one-shot timers per transition.
        _idleTimer = new System.Threading.Timer(_ => EvaluateIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void NotifySpeechActivity()
    {
        AppState? oldState = null;
        AppState newState = default;

        lock (_lock)
        {
            _lastSpeechAt = DateTimeOffset.UtcNow;
            if (_current == AppState.Idle)
            {
                oldState = _current;
                _current = AppState.Active;
                newState = _current;
            }
        }

        if (oldState.HasValue) Raise(oldState.Value, newState);
    }

    public void TogglePause()
    {
        AppState oldState;
        AppState newState;

        lock (_lock)
        {
            oldState = _current;
            newState = _current == AppState.Paused ? AppState.Idle : AppState.Paused;
            _current = newState;
            if (newState == AppState.Idle) _lastSpeechAt = DateTimeOffset.MinValue;
        }

        Raise(oldState, newState);
    }

    public void SetPaused(bool paused)
    {
        AppState oldState;
        AppState newState;

        lock (_lock)
        {
            oldState = _current;
            if (paused && _current != AppState.Paused)
            {
                newState = AppState.Paused;
            }
            else if (!paused && _current == AppState.Paused)
            {
                newState = AppState.Idle;
                _lastSpeechAt = DateTimeOffset.MinValue;
            }
            else
            {
                return;
            }
            _current = newState;
        }

        Raise(oldState, newState);
    }

    private void EvaluateIdle()
    {
        AppState? oldState = null;
        AppState newState = default;

        lock (_lock)
        {
            if (_current != AppState.Active) return;
            if (DateTimeOffset.UtcNow - _lastSpeechAt < IdleAfter) return;

            oldState = _current;
            _current = AppState.Idle;
            newState = _current;
        }

        if (oldState.HasValue) Raise(oldState.Value, newState);
    }

    private void Raise(AppState oldState, AppState newState)
    {
        if (oldState == newState) return;
        Log.Info($"State {oldState} → {newState}");
        StateChanged?.Invoke(this, new StateChangedEventArgs(oldState, newState));
    }

    public void Dispose() => _idleTimer.Dispose();
}

public sealed class StateChangedEventArgs : EventArgs
{
    public AppState OldState { get; }
    public AppState NewState { get; }

    public StateChangedEventArgs(AppState oldState, AppState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}
