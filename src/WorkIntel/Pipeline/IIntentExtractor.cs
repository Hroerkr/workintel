using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WorkIntel.Pipeline;

/// <summary>
/// Phase 3 plug-point: send a transcript to the Anthropic Claude API and parse out
/// structured intents the user just verbalized — start/stop timer, log a task, drop
/// a Slack message, etc.
/// </summary>
public interface IIntentExtractor
{
    Task<IReadOnlyList<DetectedIntent>> ExtractAsync(TranscriptionResult transcription, CancellationToken ct);
}

/// <summary>
/// One actionable item Claude found in the transcript. Targets are kept as loose
/// strings until the integration dispatcher resolves them; that lets the prompt evolve
/// without forcing a strongly-typed enum migration each time.
/// </summary>
public sealed record DetectedIntent(
    string Kind,                                         // e.g. "harvest.clock_in", "trello.create_card", "slack.post_message"
    IReadOnlyDictionary<string, string?> Parameters,     // tool-specific arguments
    double Confidence,
    string SourceQuote);                                  // the snippet of transcript that justified the intent
