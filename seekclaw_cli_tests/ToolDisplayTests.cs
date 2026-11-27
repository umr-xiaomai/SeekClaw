using SeekClaw.Cli.Ui;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Tests;

public sealed class ToolDisplayTests
{
    [Fact]
    public void TerminalInteraction_DoesNotShowTheTypedInput()
    {
        var started = new ToolCallStartedEvent("a", "command_session", "text: secret")
        {
            ArgumentsJson = """{"action":"write","text":"secret","session_id":"term"}""",
        };
        var activity = ToolDisplay.Describe(started);

        Assert.True(activity.IsTerminalInteraction);
        Assert.Equal("• Waiting for background terminal", Ansi.StripStyles(ToolDisplay.RenderHeading(activity, null, 80)));
        Assert.Equal("• Background terminal", Ansi.StripStyles(ToolDisplay.RenderHeading(activity, true, 80)));
    }

    [Fact]
    public void ToolPreview_WrapsWideTextWithinTheOutputIndent()
    {
        var rows = new List<string>();
        ToolDisplay.AppendDetail(rows, "甲乙丙丁戊己庚辛\x1b[2J\n    detail\nmore text", 14);

        Assert.Equal(["  └ 甲乙丙丁", "    戊己庚辛", "        detai", "    … +2"], rows.Select(Ansi.StripStyles));
        Assert.All(rows, line => Assert.InRange(Ansi.VisibleLength(line), 1, 13));
    }
}
