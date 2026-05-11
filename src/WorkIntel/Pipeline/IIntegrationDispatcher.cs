using System.Threading;
using System.Threading.Tasks;

namespace WorkIntel.Pipeline;

/// <summary>
/// Phase 4 plug-point: dispatch a <see cref="DetectedIntent"/> to the right
/// integration (Harvest v2, Trello, Slack Web API). Implementations should be
/// idempotent where possible — Claude will occasionally double-emit intents and
/// we never want to log a task twice.
/// </summary>
public interface IIntegrationDispatcher
{
    Task<DispatchResult> DispatchAsync(DetectedIntent intent, CancellationToken ct);
}

public sealed record DispatchResult(
    bool Success,
    string? Message,
    string? RemoteUrl);   // e.g. Trello card URL or Slack permalink, when available
