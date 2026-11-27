using System.Text.RegularExpressions;
using SeekClaw.Cli.Ui;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Tests;

public sealed class TerminalRendererTests
{
    [Fact]
    public void LiveRegion_FitsCjkToPhysicalRowsAndUpdatesOnlyTheChangedLine()
    {
        var output = new StringWriter();
        var region = new LiveRegion(output);
        var size = new TerminalSize(20, 12);
        var cjk = new string('你', 18).Style(Ansi.Cyan) + "\x1b[2J";

        region.Update(null, [cjk, "waiting"], size, cursorRowsBelow: 1, cursorColumn: 4);

        var firstFrame = output.ToString();
        var textRow = firstFrame.Split("\r\n")[0];
        Assert.Equal(new string('你', 9) + "…", Ansi.StripStyles(textRow));
        Assert.Equal(19, Ansi.VisibleLength(textRow));
        Assert.DoesNotContain("\x1b[2J", firstFrame);

        output.GetStringBuilder().Clear();
        region.Update(null, [cjk, "working"], size, cursorRowsBelow: 1, cursorColumn: 4);

        var update = output.ToString();
        Assert.DoesNotContain('你', update);
        Assert.DoesNotContain("\x1b[0J", update);
        Assert.Contains("\x1b[1B\x1b[2Kworking", update);
        Assert.EndsWith("\x1b[1A\x1b[4C", update);
    }

    [Fact]
    public void LiveRegion_ChangingHeightKeepsUnchangedRows()
    {
        var output = new StringWriter();
        var region = new LiveRegion(output);
        var size = new TerminalSize(28, 12);
        region.Update(null, ["same prefix", "second"], size);

        output.GetStringBuilder().Clear();
        region.Update(null, ["same prefix", "second", "third"], size);
        Assert.Contains("third", output.ToString());
        Assert.DoesNotContain("same prefix", output.ToString());
        Assert.DoesNotContain("second", output.ToString());

        output.GetStringBuilder().Clear();
        region.Update(null, ["same prefix"], size);
        Assert.DoesNotContain("same prefix", output.ToString());
        Assert.Equal(2, Regex.Matches(output.ToString(), "\\x1b\\[2K").Count);
    }

    [Fact]
    public void LiveRegion_ClearsRowsReflowedByConPtyOnResize()
    {
        if (!OperatingSystem.IsWindows()) return;

        var output = new StringWriter();
        var region = new LiveRegion(output);
        region.Update(null, ["", new string('你', 20), "status"], new TerminalSize(50, 20));

        output.GetStringBuilder().Clear();
        region.Update(null, ["new layout", "status"], new TerminalSize(20, 20));
        var update = output.ToString();
        // ConPTY wraps the 40-column row into two rows. The old blank row
        // therefore starts three rows above the cursor on the old status line.
        Assert.StartsWith("\x1b[?2026h\r\x1b[3A\x1b[2K", update);
        Assert.Contains("new layout", update);
    }

    [Fact]
    public void LiveRegion_AppendsOnlyNewTextAndRepaintsAnExtendedGrapheme()
    {
        var output = new StringWriter();
        var region = new LiveRegion(output);
        var size = new TerminalSize(40, 12);
        var prefix = "• ".Style(Ansi.Dim) + "Answer";
        region.Update(null, [prefix], size);
        output.GetStringBuilder().Clear();

        region.Update(null, [prefix + " e"], size);

        Assert.Contains(" e", output.ToString());
        Assert.DoesNotContain("Answer", output.ToString());
        Assert.DoesNotContain("\x1b[2K", output.ToString());
        output.GetStringBuilder().Clear();

        region.Update(null, [prefix + " e\u0301"], size);
        Assert.Contains("\x1b[2K" + prefix + " e\u0301", output.ToString());
    }

    [Fact]
    public void LiveRegion_DoesNotSplitEmojiOrCombiningMarks()
    {
        const string text = "👩‍💻e\u0301abcd";
        Assert.Equal("👩‍💻e\u0301…", Ansi.StripStyles(Ansi.TruncateVisible(text.Style(Ansi.Bold), 4)));
    }

    [Fact]
    public void ParallelToolProgress_StaysWithTheToolThatProducedIt()
    {
        var bus = new EventBus();
        var output = new StringWriter();
        using (var renderer = new TerminalRenderer(bus, output))
        {
            bus.Publish(new TurnStartedEvent("session", "hello"));
            bus.Publish(new ToolCallStartedEvent("a", "first tool", ""));
            bus.Publish(new ToolCallProgressEvent("a", "first output"));
            bus.Publish(new ToolCallStartedEvent("b", "second tool", ""));
            bus.Publish(new ToolCallProgressEvent("b", "second output"));
            bus.Publish(new ToolCallCompletedEvent("a", "first tool", true, "first done", TimeSpan.Zero));
            bus.Publish(new ToolCallProgressEvent("b", " continued"));
            bus.Publish(new ToolCallCompletedEvent("b", "second tool", true, "second done", TimeSpan.Zero));
            bus.Publish(new TurnCompletedEvent("session", false, null));
            renderer.Flush();
        }

        var rendered = Ansi.StripStyles(output.ToString());
        Assert.Contains("• Ran first tool\r\n  └ first output", rendered);
        Assert.Contains("• Ran second tool\r\n  └ second output continued", rendered);
    }

    [Fact]
    public void TerminalInteraction_LeavesQuietPollsAndInputOutOfScrollback()
    {
        var bus = new EventBus();
        var output = new StringWriter();
        using (var renderer = new TerminalRenderer(bus, output))
        {
            bus.Publish(new TurnStartedEvent("session", "prompt"));
            bus.Publish(new ToolCallStartedEvent("poll", "write_stdin", "session_id: terminal"));
            bus.Publish(new ToolCallCompletedEvent("poll", "write_stdin", true, "Command still running", TimeSpan.Zero)
            {
                Output = """{"status":"running","output":""}""",
            });
            bus.Publish(new ToolCallStartedEvent("input", "command_session", "text: input-marker")
            {
                ArgumentsJson = """{"action":"write","text":"input-marker"}""",
            });
            bus.Publish(new ToolCallCompletedEvent("input", "command_session", true, "Command still running", TimeSpan.Zero)
            {
                Output = """{"status":"running","output":"new terminal output"}""",
            });
            bus.Publish(new TurnCompletedEvent("session", false, null));
        }

        Assert.DoesNotContain("input-marker", output.ToString());
        Assert.DoesNotContain("Command still running", output.ToString());
        Assert.Single(Regex.Matches(output.ToString(), "new terminal output"));
    }

    [Fact]
    public void ConsecutiveReadCalls_FormASingleExplorationGroup()
    {
        var bus = new EventBus();
        var output = new StringWriter();
        using (var renderer = new TerminalRenderer(bus, output))
        {
            bus.Publish(new TurnStartedEvent("session", "prompt"));
            bus.Publish(new ToolCallStartedEvent("a", "read_file", "") { ArgumentsJson = """{"path":"first.cs"}""" });
            bus.Publish(new ToolCallCompletedEvent("a", "read_file", true, "first file contents", TimeSpan.Zero));
            bus.Publish(new ModelInvocationStartedEvent("provider", "model", 2));
            bus.Publish(new ToolCallStartedEvent("b", "read_file", "") { ArgumentsJson = """{"path":"second.cs"}""" });
            bus.Publish(new ToolCallCompletedEvent("b", "read_file", true, "second file contents", TimeSpan.Zero));
            bus.Publish(new AssistantMessageCompletedEvent("Done."));
            bus.Publish(new TurnCompletedEvent("session", false, null));
        }

        var rendered = Ansi.StripStyles(output.ToString());
        Assert.Contains("• Explored\r\n  └ Read first.cs, second.cs", rendered);
        Assert.DoesNotContain("file contents", rendered);
    }

    [Fact]
    public void CompletedStream_PromotesTheUnchangedPreviewWithoutWritingTheAnswerAgain()
    {
        var bus = new EventBus();
        var output = new StringWriter();
        using (var renderer = new TerminalRenderer(bus, output))
        {
            bus.Publish(new TurnStartedEvent("session", "prompt"));
            bus.Publish(new AssistantTextDeltaEvent("Rendered answer"));
            renderer.Flush();
            Assert.Contains("Rendered answer", output.ToString());

            bus.Publish(new AssistantMessageCompletedEvent("Rendered answer"));
            bus.Publish(new TurnCompletedEvent("session", false, null));
            renderer.Flush();
        }

        Assert.Single(Regex.Matches(output.ToString(), "Rendered answer"));
    }

    [Fact]
    public void CompletedStream_ShowsTheLastTableRowWithoutATrailingNewline()
    {
        const string heading = "| Key | Value |\n| --- | --- |\n";
        const string finalRow = "| alpha | beta |";
        var bus = new EventBus();
        var output = new StringWriter();
        using var renderer = new TerminalRenderer(bus, output);
        bus.Publish(new TurnStartedEvent("session", "prompt"));
        bus.Publish(new AssistantTextDeltaEvent(heading + finalRow));
        renderer.Flush();
        Assert.DoesNotContain("alpha", output.ToString());

        bus.Publish(new AssistantMessageCompletedEvent(heading + finalRow));
        bus.Publish(new TurnCompletedEvent("session", false, null));
        renderer.Flush();
        Assert.Single(Regex.Matches(Ansi.StripStyles(output.ToString()), "alpha +beta"));
    }

    [Fact]
    public void StreamReset_DiscardsTheOldAttemptAndKeepsTheNewAnswer()
    {
        var bus = new EventBus();
        var output = new StringWriter();
        using (var renderer = new TerminalRenderer(bus, output))
        {
            bus.Publish(new TurnStartedEvent("session", "prompt"));
            bus.Publish(new ThinkingDeltaEvent("uncommitted reasoning"));
            bus.Publish(new ThinkingCompletedEvent());
            bus.Publish(new AssistantTextDeltaEvent("discarded draft"));
            renderer.Flush();
            output.GetStringBuilder().Clear();

            bus.Publish(new ProviderRetryEvent("test/model", 1, "response ended", TimeSpan.Zero));
            renderer.Flush();
            Assert.Contains("retry #1", output.ToString());
            Assert.DoesNotContain("discarded draft", output.ToString());

            bus.Publish(new AssistantStreamResetEvent("test/model", 15, 21));
            bus.Publish(new AssistantTextDeltaEvent("replacement"));
            renderer.Flush();
            bus.Publish(new AssistantMessageCompletedEvent("replacement"));
            bus.Publish(new TurnCompletedEvent("session", false, null));
        }

        var rendered = output.ToString();
        Assert.DoesNotContain("discarded draft", rendered);
        Assert.DoesNotContain("uncommitted reasoning", rendered);
        Assert.Single(Regex.Matches(rendered, "replacement"));
    }

    [Fact]
    public void LongStream_EntersHistoryBeforeCompletionWithoutReplayingThePrefix()
    {
        var history = new TerminalHistory();
        var source = string.Join("\n\n", Enumerable.Range(1, 35).Select(i => $"Paragraph {i:00}"));
        var rows = AssistantCell.Render(source, 40);

        var emitted = history.ShowDraft(rows, availableRows: 9, spillableRows: rows.Count);
        Assert.Contains(history.TakeDraw(40).Rows, row => row.EndsWith("Paragraph 01", StringComparison.Ordinal));
        Assert.Equal(9, rows.Count - emitted);

        history.CompleteAssistant(source, rows);
        var completion = history.TakeDraw(40);
        Assert.False(completion.Replay);
        Assert.Equal(rows.Skip(emitted), completion.Rows);
    }

    [Fact]
    public void RepairedHistory_ReflowsCommittedMarkdownAndRemovesOnlyTheDiscardedDraft()
    {
        var history = new TerminalHistory();
        const string prior = "A previously committed answer with a long line.";
        history.Add("prior prompt");
        history.CompleteAssistant(prior, AssistantCell.Render(prior, 60));
        var draft = AssistantCell.Render("discarded first paragraph\n\ndiscarded second paragraph", 60);
        history.ShowDraft(draft, availableRows: 1, spillableRows: draft.Count);
        history.TakeDraw(60);

        history.DiscardDraft();
        history.Add("retrying");
        var repair = history.TakeDraw(20);

        Assert.True(repair.Replay);
        Assert.Equal(["prior prompt", .. AssistantCell.Render(prior, 20), "retrying"], repair.Rows);
        Assert.Empty(history.TakeDraw(20).Rows);
    }

    [Fact]
    public void LiveRegion_PromotesOnlyTheMatchingPrefixAndLeavesUnchangedInputAlone()
    {
        var output = new StringWriter();
        var region = new LiveRegion(output);
        var size = new TerminalSize(40, 12);
        region.Update(null, ["first answer row", "tail", "› composer"], size);
        output.GetStringBuilder().Clear();

        region.Update(["first answer row"], ["tail", "› composer"], size);

        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void CompletedTurn_RendersOnlyTheTurnBoundaryDivider()
    {
        var bus = new EventBus();
        var output = new StringWriter();

        using (var renderer = new TerminalRenderer(bus, output, showTurnDividers: true))
        {
            bus.Publish(new TurnStartedEvent("session", "hello"));
            bus.Publish(new ThinkingDeltaEvent("A short thought."));
            bus.Publish(new ThinkingCompletedEvent());
            bus.Publish(new AssistantMessageCompletedEvent("Hello."));
            bus.Publish(new TurnCompletedEvent("session", false, null));
            renderer.Flush();
        }

        var rendered = output.ToString();
        Assert.DoesNotContain("═══", rendered);
        Assert.DoesNotContain("A short thought.", rendered);
        Assert.Single(Regex.Matches(rendered, "─{20,}").Cast<Match>());
    }
}
