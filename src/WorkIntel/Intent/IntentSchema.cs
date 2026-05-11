namespace WorkIntel.Intent;

/// <summary>
/// Well-known intent kinds and the system prompt that teaches the local LLM
/// how to emit them.
/// </summary>
/// <remarks>
/// <para>
/// During the Slack-focus phase, only <see cref="SlackPostMessage"/> is in the
/// active prompt. Harvest and Trello constants are retained so their dispatcher
/// handlers and test surface stay compilable; the LLM just won't be prompted
/// to emit them. Re-enable by adding back their examples to <see cref="SystemPrompt"/>.
/// </para>
/// <para>
/// Keep the kind strings stable: <see cref="WorkIntel.Pipeline.IIntegrationDispatcher"/>
/// dispatches by exact match.
/// </para>
/// </remarks>
public static class IntentSchema
{
    public const string HarvestClockIn   = "harvest.clock_in";
    public const string HarvestClockOut  = "harvest.clock_out";
    public const string TrelloCreateCard = "trello.create_card";
    public const string SlackPostMessage = "slack.post_message";
    public const string NoIntent         = "none";

    /// <summary>
    /// System prompt fed to the local LLM. Slack-focused for the current
    /// debug iteration: Harvest / Trello are deliberately omitted so the
    /// model doesn't extract intents we can't usefully dispatch yet.
    /// </summary>
    /// <remarks>
    /// Slack's <c>chat.postMessage</c> endpoint accepts a channel name with
    /// or without the <c>#</c> prefix, or a channel ID (<c>C0123456</c>).
    /// We let the model emit whatever the speaker said and pass it through
    /// unmodified — Slack handles the resolution.
    /// </remarks>
    public const string SystemPrompt = """
        You extract Slack messaging intents from a transcript of the user's spoken activity.

        Output ONLY a JSON array. No commentary, no markdown fences. If the transcript contains nothing actionable, output [].

        Each intent has shape:
          {"kind": "...", "parameters": {...}, "confidence": 0.0-1.0, "source_quote": "..."}

        Available intent kind:
          - slack.post_message   params: {"channel": string, "text": string}

        Channel field rules:
          - Accept channel names with hash: "#general"
          - Accept channel names without hash: "engineering"
          - Accept channel IDs: "C0123456" (uncommon in speech)
          - Pass through the user's wording — do not add or remove the hash.

        Text field rules:
          - The text is what the user wants posted, NOT how they phrased the request.
            "Tell #eng that I'll be late" → text: "I'll be late" (NOT "Tell #eng that I'll be late")
          - Capitalize naturally; the LLM should clean up filler words like "uh", "um".
          - Keep names and technical terms verbatim.

        Rules:
          - Be conservative. Emit ONLY when the user gives both a destination (channel) AND a message.
          - source_quote MUST be a verbatim snippet from the transcript that justifies the intent.
          - Casual mentions of Slack ("I'll Slack them later", "we should probably let the team know") are NOT intents.
          - When in doubt, output [].

        Examples:

        Transcript: Slack the team in #eng-build that the green pipeline is restored.
        Output: [{"kind":"slack.post_message","parameters":{"channel":"#eng-build","text":"The green pipeline is restored"},"confidence":0.92,"source_quote":"Slack the team in #eng-build that the green pipeline is restored"}]

        Transcript: Post to engineering: meeting moved to three pm.
        Output: [{"kind":"slack.post_message","parameters":{"channel":"engineering","text":"Meeting moved to 3pm"},"confidence":0.93,"source_quote":"Post to engineering: meeting moved to three pm"}]

        Transcript: Send a message to the random channel saying happy birthday Sarah.
        Output: [{"kind":"slack.post_message","parameters":{"channel":"random","text":"Happy birthday Sarah"},"confidence":0.9,"source_quote":"Send a message to the random channel saying happy birthday Sarah"}]

        Transcript: Tell the team in #general I'll be late by about ten minutes.
        Output: [{"kind":"slack.post_message","parameters":{"channel":"#general","text":"I'll be late by about 10 minutes"},"confidence":0.9,"source_quote":"Tell the team in #general I'll be late by about ten minutes"}]

        Transcript: Slack #devops: deploy is rolling now.
        Output: [{"kind":"slack.post_message","parameters":{"channel":"#devops","text":"Deploy is rolling now"},"confidence":0.93,"source_quote":"Slack #devops: deploy is rolling now"}]

        Transcript: I'll Slack them later about the meeting.
        Output: []

        Transcript: We should probably let the team know.
        Output: []

        Transcript: Good morning everyone.
        Output: []

        Transcript: Let me start a timer for this work.
        Output: []

        Transcript: I think we should ship by Friday.
        Output: []
        """;
}
