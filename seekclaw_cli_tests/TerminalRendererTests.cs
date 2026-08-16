using System.Text.RegularExpressions;
using SeekClaw.Cli.Ui;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Tests;

public sealed class TerminalRendererTests
{
    [Fact]
    public void StreamingMarkdown_RendersFencedCodeWithoutRawFenceMarkers()
    {
        var renderer = new MarkdownStreamRenderer();

        var lines = renderer.Render(
            "```csharp\nConsole.WriteLine(\"hello\");\n```",
            width: 80,
            maxLines: 20);

        Assert.Contains(lines, line => Ansi.StripStyles(line).Contains("Console.WriteLine"));
        Assert.DoesNotContain(lines, line => Ansi.StripStyles(line).Contains("```"));
    }

    [Fact]
    public void StreamingMarkdown_RendersHeadingBeforeTheBlockIsFinalized()
    {
        var renderer = new MarkdownStreamRenderer();

        var lines = renderer.Render("# In progress\n\nstill writing", width: 80, maxLines: 20);

        Assert.Contains(lines, line => Ansi.StripStyles(line).Contains("In progress"));
        Assert.DoesNotContain(lines, line => Ansi.StripStyles(line).StartsWith("# "));
    }

    [Fact]
    public void StableTail_WrapsCjkAndKeepsLatestRows()
    {
        const string text = "这是较早的思考内容。这里是接近结尾的分析，需要稳定显示而不是每帧消失。";

        var preview = TerminalRenderer.BuildStableTail(text, lineWidth: 16, maxLines: 2);

        Assert.Equal(2, preview.Count);
        Assert.Contains("每帧消失", string.Concat(preview));
        Assert.All(preview, line => Assert.InRange(TextWidth.Of(line), 1, 16));
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
        Assert.DoesNotContain("Thinking Complete", rendered);
        Assert.Single(Regex.Matches(rendered, "─{20,}").Cast<Match>());
    }
}
