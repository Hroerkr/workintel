namespace WorkIntel.Intent;

/// <summary>
/// System prompt + JSON schema for the local LLM. The model reads a single
/// signal (a transcript, an email, a Slack message) and emits a JSON array of
/// task candidates — things the user would treat as actionable.
/// </summary>
/// <remarks>
/// <para>
/// Source-agnostic by design. The signal-emitter tags the row with where it
/// came from after the fact; the LLM doesn't need to know whether it's reading
/// a transcript or an inbox. Source-specific prompt tuning can come later if
/// quality differs meaningfully per channel.
/// </para>
/// <para>
/// Negative examples carry most of the conservatism — first iteration with
/// real YouTube audio will produce false positives, and the cheapest fix is
/// adding the offending snippet as a new negative example here.
/// </para>
/// </remarks>
public static class IntentSchema
{
    // Kept for the (currently vestigial) dispatcher code to still compile.
    // Safe to delete once Integrations/ is removed.
    public const string HarvestClockIn   = "harvest.clock_in";
    public const string HarvestClockOut  = "harvest.clock_out";
    public const string TrelloCreateCard = "trello.create_card";
    public const string SlackPostMessage = "slack.post_message";
    public const string NoIntent         = "none";

    public const string SystemPrompt = """
        You read short snippets of communication (a spoken transcript, an email, a chat message)
        and extract action items the listener/reader would treat as a task to do.

        Output ONLY a JSON array. No commentary, no markdown fences. If the snippet contains
        nothing actionable, output [].

        Each task has shape:
          {
            "title": string,            // short imperative summary, e.g. "Fix export bug"
            "description": string?,     // additional detail, when the snippet gives more context
            "owner": string?,           // "me" | sender's name | named person, when clearly implied
            "deadline": string?,        // natural-language or ISO date, when explicitly mentioned
            "confidence": 0.0-1.0,
            "source_quote": string      // verbatim snippet that justifies this task
          }

        What counts as a task:
          - A request to do something ("Can you …", "Please …", "I need …")
          - A commitment from the user ("I'll handle …", "I'll have it ready by …")
          - An explicit follow-up agreed in conversation ("Let's circle back on X tomorrow")

        What does NOT count as a task:
          - Status updates and information sharing ("The deploy went out at 3am")
          - Opinions, reactions, social pleasantries ("Great work today", "Good morning")
          - Speculation, hypotheticals ("We could maybe look at X", "It might be worth …")
          - Generic mentions of work without an actionable ask ("The Q3 numbers were up")
          - Anything the user is only listening to passively (a podcast, a video, a meeting recap)

        Rules:
          - Be conservative. When in doubt, output [].
          - source_quote MUST be verbatim from the input.
          - "I'll handle X" → owner: "me".
          - "Can you handle X?" addressed to the user → owner: "me".
          - "Bob is handling X" → not a task on the user. Output [].
          - Re-phrase the title in clean imperative form even if the source is verbose.

        Examples:

        Input: "Hey, can you take a look at the export bug today? It's blocking the team."
        Output: [{"title":"Look at the export bug","description":"Blocking the team","owner":"me","deadline":"today","confidence":0.92,"source_quote":"can you take a look at the export bug today"}]

        Input: "I'll have the design review notes written up by Friday."
        Output: [{"title":"Write up design review notes","owner":"me","deadline":"Friday","confidence":0.9,"source_quote":"I'll have the design review notes written up by Friday"}]

        Input: "The deploy went out at 3am and the metrics look fine."
        Output: []

        Input: "Good morning everyone, hope you had a great weekend."
        Output: []

        Input: "We could maybe revisit the caching strategy at some point."
        Output: []

        Input: "Please update the runbook to mention the new fail-over procedure."
        Output: [{"title":"Update runbook with new fail-over procedure","owner":"me","confidence":0.88,"source_quote":"Please update the runbook to mention the new fail-over procedure"}]

        Input: "Bob is handling the migration to the new auth service."
        Output: []

        Input: "Let's circle back on the API design next Tuesday."
        Output: [{"title":"Follow up on API design","deadline":"next Tuesday","confidence":0.7,"source_quote":"Let's circle back on the API design next Tuesday"}]

        Input: "I love the new dashboard, it's so much cleaner."
        Output: []
        """;
}
