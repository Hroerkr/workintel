namespace WorkIntel.Tasks;

/// <summary>
/// What the LLM extracts from a single signal (a transcript, an email body, a
/// Slack message). Pre-persistence shape — has no id/status/timestamps because
/// the server stamps those when the row is created.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Owner"/> field is best-effort interpretation: "me" when the
/// listener/reader is implied responsible, the sender's name when they
/// committed, a named third party when explicitly mentioned. Null means the
/// LLM couldn't infer one.
/// </para>
/// <para>
/// <see cref="Deadline"/> is intentionally a free-form string ("Friday",
/// "next week", "2026-05-15"). Normalisation to a real date can happen later
/// once we know it matters for filtering.
/// </para>
/// </remarks>
public sealed record TaskCandidate(
    string Title,
    string? Description,
    string? Owner,
    string? Deadline,
    double Confidence,
    string SourceQuote);
