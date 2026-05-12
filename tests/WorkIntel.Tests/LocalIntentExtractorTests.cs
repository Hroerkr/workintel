using WorkIntel.Intent;
using Xunit;

namespace WorkIntel.Tests;

/// <summary>
/// Exercises <see cref="LocalIntentExtractor.ParseTasks"/> only. The full
/// extractor requires a multi-GB model load and is exercised manually.
/// </summary>
public sealed class LocalIntentExtractorTests
{
    [Fact]
    public void WellFormedJson_ParsesCleanly()
    {
        const string raw = """
            [{"title":"Fix export bug","owner":"me","deadline":"today","confidence":0.92,"source_quote":"fix the export bug today"}]
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        var t = tasks[0];
        Assert.Equal("Fix export bug", t.Title);
        Assert.Equal("me", t.Owner);
        Assert.Equal("today", t.Deadline);
        Assert.Equal(0.92, t.Confidence, 2);
        Assert.Equal("fix the export bug today", t.SourceQuote);
    }

    [Fact]
    public void OptionalFieldsAbsent_StillParses()
    {
        const string raw = """[{"title":"Update runbook","confidence":0.8,"source_quote":"update the runbook"}]""";

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        Assert.Null(tasks[0].Description);
        Assert.Null(tasks[0].Owner);
        Assert.Null(tasks[0].Deadline);
    }

    [Fact]
    public void JsonWrappedInMarkdownFence_IsExtracted()
    {
        const string raw = """
            ```json
            [{"title":"Write up notes","confidence":0.7,"source_quote":"write up the notes"}]
            ```
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        Assert.Equal("Write up notes", tasks[0].Title);
    }

    [Fact]
    public void TrailingCommentary_IsIgnored()
    {
        const string raw = """
            Sure! Here's the JSON:
            [{"title":"Review PR","confidence":0.85,"source_quote":"review the PR"}]
            Hope that helps.
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        Assert.Equal("Review PR", tasks[0].Title);
    }

    [Fact]
    public void EmptyArray_ProducesNoTasks()
    {
        Assert.Empty(LocalIntentExtractor.ParseTasks("[]"));
    }

    [Fact]
    public void EmptyOrWhitespaceInput_ProducesNoTasks()
    {
        Assert.Empty(LocalIntentExtractor.ParseTasks(""));
        Assert.Empty(LocalIntentExtractor.ParseTasks("   "));
    }

    [Fact]
    public void NonsenseInput_ProducesNoTasks()
    {
        Assert.Empty(LocalIntentExtractor.ParseTasks("the model decided to refuse"));
    }

    [Fact]
    public void MalformedJsonAfterStart_DoesNotThrow()
    {
        const string raw = "[{\"title\":\"Fix bug\", broken";
        var tasks = LocalIntentExtractor.ParseTasks(raw);
        Assert.Empty(tasks);
    }

    [Fact]
    public void EntriesWithBlankTitle_AreFilteredOut()
    {
        const string raw = """
            [{"title":"","confidence":0.1,"source_quote":""},
             {"title":"   ","confidence":0.1,"source_quote":""},
             {"title":"Real task","confidence":0.9,"source_quote":"x"}]
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        Assert.Equal("Real task", tasks[0].Title);
    }

    [Fact]
    public void MultipleTasks_AreAllReturned()
    {
        const string raw = """
            [
              {"title":"Task A","confidence":0.9,"source_quote":"a"},
              {"title":"Task B","confidence":0.8,"source_quote":"b"}
            ]
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("Task A", tasks[0].Title);
        Assert.Equal("Task B", tasks[1].Title);
    }

    [Fact]
    public void StringContainingBracket_DoesNotConfuseDepthCounter()
    {
        const string raw = """
            [{"title":"Investigate [link] in email","confidence":0.7,"source_quote":"see [link]"}]
            """;

        var tasks = LocalIntentExtractor.ParseTasks(raw);

        Assert.Single(tasks);
        Assert.Contains("[link]", tasks[0].Title);
    }
}
