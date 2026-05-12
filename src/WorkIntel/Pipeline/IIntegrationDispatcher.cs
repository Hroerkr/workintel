using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WorkIntel.Pipeline;

/// <summary>
/// Vestigial outbound dispatcher interface from the pre-pivot design. Kept
/// alongside its implementations under <c>Integrations/</c> so the old code
/// still compiles, but no longer wired into the live pipeline.
/// </summary>
public interface IIntegrationDispatcher
{
    Task<DispatchResult> DispatchAsync(DetectedIntent intent, CancellationToken ct);
}

public sealed record DispatchResult(
    bool Success,
    string? Message,
    string? RemoteUrl);

/// <summary>
/// Old intent shape (kind + parameters). Retained for the dispatcher code; new
/// task-extraction path emits <see cref="WorkIntel.Tasks.TaskCandidate"/>.
/// </summary>
public sealed record DetectedIntent(
    string Kind,
    IReadOnlyDictionary<string, string?> Parameters,
    double Confidence,
    string SourceQuote);
