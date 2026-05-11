using System;
using System.Collections.Generic;
using System.Threading;
using WorkIntel.Integrations;
using WorkIntel.Intent;
using WorkIntel.Pipeline;
using Xunit;

namespace WorkIntel.Tests;

/// <summary>
/// Idempotency / dedup tests. Network-touching tests (DispatchAsync calling out
/// to Harvest etc.) are excluded — those are integration territory.
/// </summary>
public sealed class IntegrationDispatcherTests
{
    private static IntegrationDispatcher NewDispatcher(TimeSpan? window = null)
    {
        // Empty secrets — clients will report IsConfigured=false but the dedup
        // primitives we're testing don't reach the network.
        var d = new IntegrationDispatcher(
            new HarvestClient(new HarvestSecrets()),
            new TrelloClient(new TrelloSecrets()),
            new SlackClient(new SlackSecrets()))
        {
            DedupeWindow = window ?? TimeSpan.FromMinutes(1),
        };
        return d;
    }

    private static DetectedIntent Intent(string kind, params (string k, string? v)[] paramz)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in paramz) dict[k] = v;
        return new DetectedIntent(kind, dict, 0.9, "src");
    }

    // ─── ComputeKey ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeKey_SameKindAndParams_YieldsSameKey()
    {
        var a = Intent(IntentSchema.HarvestClockIn, ("task", "Acme"));
        var b = Intent(IntentSchema.HarvestClockIn, ("task", "Acme"));

        Assert.Equal(IntegrationDispatcher.ComputeKey(a), IntegrationDispatcher.ComputeKey(b));
    }

    [Fact]
    public void ComputeKey_DifferentKind_YieldsDifferentKey()
    {
        var a = Intent(IntentSchema.HarvestClockIn, ("task", "Acme"));
        var b = Intent(IntentSchema.HarvestClockOut);

        Assert.NotEqual(IntegrationDispatcher.ComputeKey(a), IntegrationDispatcher.ComputeKey(b));
    }

    [Fact]
    public void ComputeKey_ParamOrder_DoesNotMatter()
    {
        var a = Intent(IntentSchema.TrelloCreateCard, ("title", "Fix"), ("list", "Bugs"));
        var b = Intent(IntentSchema.TrelloCreateCard, ("list", "Bugs"), ("title", "Fix"));

        Assert.Equal(IntegrationDispatcher.ComputeKey(a), IntegrationDispatcher.ComputeKey(b));
    }

    [Fact]
    public void ComputeKey_ParamCase_IsNormalized()
    {
        var a = Intent(IntentSchema.SlackPostMessage, ("channel", "#General"));
        var b = Intent(IntentSchema.SlackPostMessage, ("channel", "#general"));

        Assert.Equal(IntegrationDispatcher.ComputeKey(a), IntegrationDispatcher.ComputeKey(b));
    }

    [Fact]
    public void ComputeKey_DifferentParamValues_YieldDifferentKeys()
    {
        var a = Intent(IntentSchema.SlackPostMessage, ("channel", "#a"));
        var b = Intent(IntentSchema.SlackPostMessage, ("channel", "#b"));

        Assert.NotEqual(IntegrationDispatcher.ComputeKey(a), IntegrationDispatcher.ComputeKey(b));
    }

    // ─── IsDuplicate ──────────────────────────────────────────────────────

    [Fact]
    public void IsDuplicate_FirstCall_ReturnsFalse()
    {
        using var d = NewDispatcher();
        var i = Intent(IntentSchema.HarvestClockIn, ("task", "x"));

        Assert.False(d.IsDuplicate(i));
    }

    [Fact]
    public void IsDuplicate_SecondIdenticalCall_ReturnsTrue()
    {
        using var d = NewDispatcher();
        var i = Intent(IntentSchema.HarvestClockIn, ("task", "x"));

        Assert.False(d.IsDuplicate(i));
        Assert.True(d.IsDuplicate(i));
    }

    [Fact]
    public void IsDuplicate_DifferentParams_NotDeduped()
    {
        using var d = NewDispatcher();
        var a = Intent(IntentSchema.HarvestClockIn, ("task", "x"));
        var b = Intent(IntentSchema.HarvestClockIn, ("task", "y"));

        Assert.False(d.IsDuplicate(a));
        Assert.False(d.IsDuplicate(b));
    }

    [Fact]
    public void IsDuplicate_AfterShortWindow_AllowsRepeat()
    {
        using var d = NewDispatcher(window: TimeSpan.FromMilliseconds(50));
        var i = Intent(IntentSchema.HarvestClockIn, ("task", "x"));

        Assert.False(d.IsDuplicate(i));
        Thread.Sleep(150); // safely past 50ms window
        Assert.False(d.IsDuplicate(i));
    }
}
