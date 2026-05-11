using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;
using WorkIntel.Intent;
using WorkIntel.Pipeline;

namespace WorkIntel.Integrations;

/// <summary>
/// Routes <see cref="DetectedIntent"/>s to the matching integration client.
/// Maintains an in-memory idempotency cache so duplicate intents emitted by
/// Whisper/Phi-3.5 (e.g. when a sentence is split across two segments) don't
/// double-fire actions.
/// </summary>
public sealed class IntegrationDispatcher : IIntegrationDispatcher, IDisposable
{
    // Mutable so Reload can swap them out without rebuilding the dispatcher.
    private HarvestClient _harvest;
    private TrelloClient _trello;
    private SlackClient _slack;

    /// <summary>Idempotency window — duplicate intents within this period are deduped.</summary>
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private readonly object _seenLock = new();

    public IntegrationDispatcher(HarvestClient harvest, TrelloClient trello, SlackClient slack)
    {
        _harvest = harvest;
        _trello  = trello;
        _slack   = slack;
    }

    public async Task<DispatchResult> DispatchAsync(DetectedIntent intent, CancellationToken ct)
    {
        if (IsDuplicate(intent))
        {
            return new DispatchResult(Success: true, Message: "deduped (recent identical intent)", RemoteUrl: null);
        }

        try
        {
            return intent.Kind switch
            {
                IntentSchema.HarvestClockIn   => await DispatchHarvestClockIn(intent, ct).ConfigureAwait(false),
                IntentSchema.HarvestClockOut  => await DispatchHarvestClockOut(intent, ct).ConfigureAwait(false),
                IntentSchema.TrelloCreateCard => await DispatchTrelloCreate(intent, ct).ConfigureAwait(false),
                IntentSchema.SlackPostMessage => await DispatchSlackPost(intent, ct).ConfigureAwait(false),
                _ => new DispatchResult(false, $"unknown intent kind: {intent.Kind}", null),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"dispatch {intent.Kind} threw", ex);
            return new DispatchResult(false, ex.Message, null);
        }
    }

    private async Task<DispatchResult> DispatchHarvestClockIn(DetectedIntent intent, CancellationToken ct)
    {
        if (!_harvest.IsConfigured)
            return new DispatchResult(false, "harvest not configured", null);

        long? projectId = ResolveLong(intent.Parameters, "project_id") ?? _harvest.DefaultProjectId;
        long? taskId    = ResolveLong(intent.Parameters, "task_id")    ?? _harvest.DefaultTaskId;
        if (projectId is null || taskId is null)
            return new DispatchResult(false, "harvest defaultProjectId/defaultTaskId not set in config", null);

        intent.Parameters.TryGetValue("task", out var taskNote);
        var entry = await _harvest.ClockInAsync(projectId.Value, taskId.Value, taskNote, ct).ConfigureAwait(false);
        return new DispatchResult(
            Success: true,
            Message: $"harvest entry {entry?.Id} started ({taskNote ?? "no notes"})",
            RemoteUrl: null);
    }

    private async Task<DispatchResult> DispatchHarvestClockOut(DetectedIntent _, CancellationToken ct)
    {
        if (!_harvest.IsConfigured)
            return new DispatchResult(false, "harvest not configured", null);
        var stopped = await _harvest.ClockOutAsync(ct).ConfigureAwait(false);
        return stopped is null
            ? new DispatchResult(true, "no running timer to stop", null)
            : new DispatchResult(true, $"harvest entry {stopped.Id} stopped", null);
    }

    private async Task<DispatchResult> DispatchTrelloCreate(DetectedIntent intent, CancellationToken ct)
    {
        if (!_trello.IsConfigured)
            return new DispatchResult(false, "trello not configured", null);

        if (!intent.Parameters.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
            return new DispatchResult(false, "trello.create_card requires a title", null);

        // The model emits `list` as a name (e.g. "Bugs"); we'd need a board → list map to resolve.
        // For now, if the secrets file has a defaultListId, use it; otherwise fail clearly.
        var listId = _trello.DefaultListId;
        if (string.IsNullOrWhiteSpace(listId))
            return new DispatchResult(false, "trello defaultListId not set; list-name resolution comes in Phase 4", null);

        intent.Parameters.TryGetValue("description", out var description);
        var card = await _trello.CreateCardAsync(listId!, title!, description, ct).ConfigureAwait(false);
        return new DispatchResult(
            Success: true,
            Message: $"trello card created: {card?.Name}",
            RemoteUrl: card?.ShortUrl ?? card?.Url);
    }

    private async Task<DispatchResult> DispatchSlackPost(DetectedIntent intent, CancellationToken ct)
    {
        if (!_slack.IsConfigured)
            return new DispatchResult(false, "slack not configured", null);

        if (!intent.Parameters.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
            return new DispatchResult(false, "slack.post_message requires text", null);

        intent.Parameters.TryGetValue("channel", out var channel);
        channel = NonEmpty(channel) ?? _slack.DefaultChannel;
        if (string.IsNullOrWhiteSpace(channel))
            return new DispatchResult(false, "slack channel missing and no default configured", null);

        var resp = await _slack.PostMessageAsync(channel!, text!, ct).ConfigureAwait(false);
        // Slack permalink format requires a workspace domain; for the scaffold we return the channel + ts.
        return new DispatchResult(
            Success: true,
            Message: $"slack message posted to {resp?.Channel}",
            RemoteUrl: null);
    }

    private static long? ResolveLong(IReadOnlyDictionary<string, string?> p, string key)
        => p.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? l : null;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Idempotency check: hash the kind + sorted parameter pairs into a stable key, and skip if the
    /// same key has been seen within <see cref="DedupeWindow"/>. Internal so tests can drive it.
    /// </summary>
    internal bool IsDuplicate(DetectedIntent intent)
    {
        string key = ComputeKey(intent);
        var now = DateTimeOffset.UtcNow;
        lock (_seenLock)
        {
            // Cheap GC of stale entries.
            if (_seen.Count > 200)
            {
                var cutoff = now - DedupeWindow;
                var stale = new List<string>();
                foreach (var (k, t) in _seen) if (t < cutoff) stale.Add(k);
                foreach (var k in stale) _seen.Remove(k);
            }

            if (_seen.TryGetValue(key, out var seenAt) && now - seenAt < DedupeWindow)
                return true;
            _seen[key] = now;
            return false;
        }
    }

    /// <summary>Stable hash of (kind, sorted params). Internal for tests.</summary>
    internal static string ComputeKey(DetectedIntent intent)
    {
        var sb = new StringBuilder(intent.Kind);
        var sortedKeys = new List<string>(intent.Parameters.Keys);
        sortedKeys.Sort(StringComparer.Ordinal);
        foreach (var k in sortedKeys)
        {
            sb.Append('|').Append(k).Append('=');
            sb.Append((intent.Parameters[k] ?? string.Empty).Trim().ToLowerInvariant());
        }
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Hot-swap integration credentials without restarting the app. Old clients are
    /// disposed after a short grace period so any in-flight dispatch can finish.
    /// </summary>
    public void Reload(IntegrationSecrets secrets)
    {
        var newHarvest = new HarvestClient(secrets.Harvest ?? new HarvestSecrets());
        var newTrello  = new TrelloClient(secrets.Trello ?? new TrelloSecrets());
        var newSlack   = new SlackClient(secrets.Slack ?? new SlackSecrets());

        var oldHarvest = System.Threading.Interlocked.Exchange(ref _harvest, newHarvest);
        var oldTrello  = System.Threading.Interlocked.Exchange(ref _trello, newTrello);
        var oldSlack   = System.Threading.Interlocked.Exchange(ref _slack, newSlack);

        // Drop the dedupe cache — credential change typically means "different workspace".
        lock (_seenLock) _seen.Clear();

        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            try { oldHarvest.Dispose(); } catch { }
            try { oldTrello.Dispose();  } catch { }
            try { oldSlack.Dispose();   } catch { }
        });

        Log.Info("integration dispatcher reloaded with new credentials");
    }

    public void Dispose()
    {
        _harvest.Dispose();
        _trello.Dispose();
        _slack.Dispose();
    }
}
