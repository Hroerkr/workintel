using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.Tasks;

namespace WorkIntel.Pipeline;

/// <summary>
/// Plug-point for the local LLM. Given a transcribed (or otherwise textual)
/// signal, return zero or more task candidates the user might want to act on.
/// </summary>
/// <remarks>
/// Interface name kept as <c>IIntentExtractor</c> for now to minimize churn;
/// the output shape has shifted from intent-style (kind + parameters) to
/// task-candidate-style as part of the input-sources + task-store pivot.
/// </remarks>
public interface IIntentExtractor
{
    Task<IReadOnlyList<TaskCandidate>> ExtractAsync(TranscriptionResult transcription, CancellationToken ct);
}
