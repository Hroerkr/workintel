using System;
using System.Collections.Generic;
using WorkIntel.App;
using Xunit;

namespace WorkIntel.Tests;

public sealed class StateManagerTests
{
    [Fact]
    public void InitialState_IsIdle()
    {
        using var sm = new StateManager();
        Assert.Equal(AppState.Idle, sm.Current);
    }

    [Fact]
    public void NotifySpeechActivity_TransitionsIdleToActive()
    {
        using var sm = new StateManager();
        var transitions = CollectStateChanges(sm);

        sm.NotifySpeechActivity();

        Assert.Equal(AppState.Active, sm.Current);
        Assert.Single(transitions);
        Assert.Equal(AppState.Idle, transitions[0].OldState);
        Assert.Equal(AppState.Active, transitions[0].NewState);
    }

    [Fact]
    public void NotifySpeechActivity_FromActive_DoesNotRetransition()
    {
        using var sm = new StateManager();
        sm.NotifySpeechActivity();
        var transitions = CollectStateChanges(sm);

        sm.NotifySpeechActivity();
        sm.NotifySpeechActivity();

        Assert.Equal(AppState.Active, sm.Current);
        Assert.Empty(transitions); // no extra Active→Active events
    }

    [Fact]
    public void TogglePause_FromIdle_GoesToPaused()
    {
        using var sm = new StateManager();
        sm.TogglePause();
        Assert.Equal(AppState.Paused, sm.Current);
    }

    [Fact]
    public void TogglePause_FromPaused_GoesToIdle()
    {
        using var sm = new StateManager();
        sm.TogglePause(); // → Paused
        sm.TogglePause(); // → Idle
        Assert.Equal(AppState.Idle, sm.Current);
    }

    [Fact]
    public void TogglePause_FromActive_GoesToPaused_AndResumeReturnsToIdle()
    {
        using var sm = new StateManager();
        sm.NotifySpeechActivity(); // Idle → Active
        Assert.Equal(AppState.Active, sm.Current);

        sm.TogglePause();
        Assert.Equal(AppState.Paused, sm.Current);

        sm.TogglePause();
        Assert.Equal(AppState.Idle, sm.Current); // pause-resume always lands on Idle
    }

    [Fact]
    public void NotifySpeechActivity_WhilePaused_DoesNotChangeState()
    {
        using var sm = new StateManager();
        sm.TogglePause();
        var transitions = CollectStateChanges(sm);

        sm.NotifySpeechActivity();
        sm.NotifySpeechActivity();

        Assert.Equal(AppState.Paused, sm.Current);
        Assert.Empty(transitions);
    }

    [Fact]
    public void SetPaused_True_FromIdle_GoesToPaused()
    {
        using var sm = new StateManager();
        sm.SetPaused(true);
        Assert.Equal(AppState.Paused, sm.Current);
    }

    [Fact]
    public void SetPaused_True_WhenAlreadyPaused_IsNoOp()
    {
        using var sm = new StateManager();
        sm.SetPaused(true);
        var transitions = CollectStateChanges(sm);

        sm.SetPaused(true);

        Assert.Empty(transitions);
        Assert.Equal(AppState.Paused, sm.Current);
    }

    [Fact]
    public void SetPaused_False_WhenAlreadyUnpaused_IsNoOp()
    {
        using var sm = new StateManager();
        var transitions = CollectStateChanges(sm);

        sm.SetPaused(false);

        Assert.Empty(transitions);
        Assert.Equal(AppState.Idle, sm.Current);
    }

    private static List<StateChangedEventArgs> CollectStateChanges(StateManager sm)
    {
        var transitions = new List<StateChangedEventArgs>();
        sm.StateChanged += (_, e) => transitions.Add(e);
        return transitions;
    }
}
