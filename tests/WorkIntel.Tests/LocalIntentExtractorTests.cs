using System.Linq;
using WorkIntel.Intent;
using Xunit;

namespace WorkIntel.Tests;

/// <summary>
/// Exercises <see cref="LocalIntentExtractor.ParseIntents"/> only. The full extractor
/// requires a multi-GB model load and is exercised manually / in integration tests.
/// </summary>
public sealed class LocalIntentExtractorTests
{
    [Fact]
    public void WellFormedJson_ParsesCleanly()
    {
        const string raw = """
            [{"kind":"harvest.clock_in","parameters":{"task":"Acme refactor"},"confidence":0.91,"source_quote":"start a timer"}]
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Single(intents);
        var i = intents[0];
        Assert.Equal(IntentSchema.HarvestClockIn, i.Kind);
        Assert.Equal("Acme refactor", i.Parameters["task"]);
        Assert.Equal(0.91, i.Confidence, 2);
        Assert.Equal("start a timer", i.SourceQuote);
    }

    [Fact]
    public void JsonWrappedInMarkdownFence_IsExtracted()
    {
        const string raw = """
            ```json
            [{"kind":"slack.post_message","parameters":{"channel":"#eng","text":"green"},"confidence":0.8,"source_quote":"slack"}]
            ```
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Single(intents);
        Assert.Equal(IntentSchema.SlackPostMessage, intents[0].Kind);
        Assert.Equal("#eng", intents[0].Parameters["channel"]);
    }

    [Fact]
    public void TrailingCommentary_IsIgnored()
    {
        const string raw = """
            Sure! Here's the JSON:
            [{"kind":"trello.create_card","parameters":{"title":"Fix"},"confidence":0.9,"source_quote":"create"}]
            Hope that helps.
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Single(intents);
        Assert.Equal(IntentSchema.TrelloCreateCard, intents[0].Kind);
    }

    [Fact]
    public void NoneIntents_AreFilteredOut()
    {
        const string raw = """
            [{"kind":"none","parameters":{},"confidence":0.1,"source_quote":""}]
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Empty(intents);
    }

    [Fact]
    public void EmptyArray_ProducesNoIntents()
    {
        var intents = LocalIntentExtractor.ParseIntents("[]");
        Assert.Empty(intents);
    }

    [Fact]
    public void EmptyOrWhitespaceInput_ProducesNoIntents()
    {
        Assert.Empty(LocalIntentExtractor.ParseIntents(""));
        Assert.Empty(LocalIntentExtractor.ParseIntents("   "));
    }

    [Fact]
    public void NonsenseInput_ProducesNoIntents()
    {
        Assert.Empty(LocalIntentExtractor.ParseIntents("the model decided to refuse"));
    }

    [Fact]
    public void MalformedJsonAfterStart_DoesNotThrow()
    {
        const string raw = "[{\"kind\":\"harvest.clock_in\", broken";

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Empty(intents); // graceful, not exceptional
    }

    [Fact]
    public void MultipleIntents_AreAllReturned()
    {
        const string raw = """
            [
              {"kind":"harvest.clock_in","parameters":{"task":"a"},"confidence":0.9,"source_quote":"x"},
              {"kind":"trello.create_card","parameters":{"title":"b"},"confidence":0.8,"source_quote":"y"}
            ]
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Equal(2, intents.Count);
        Assert.Equal(IntentSchema.HarvestClockIn, intents[0].Kind);
        Assert.Equal(IntentSchema.TrelloCreateCard, intents[1].Kind);
    }

    [Fact]
    public void StringContainingBracket_DoesNotConfuseDepthCounter()
    {
        // The closing bracket inside the quoted string must not be counted as
        // closing the outer array.
        const string raw = """
            [{"kind":"slack.post_message","parameters":{"channel":"#x","text":"see [link]"},"confidence":0.7,"source_quote":"q"}]
            """;

        var intents = LocalIntentExtractor.ParseIntents(raw);

        Assert.Single(intents);
        Assert.Equal("see [link]", intents[0].Parameters["text"]);
    }
}
