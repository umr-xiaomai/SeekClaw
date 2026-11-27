using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class PromptSurfaceTests
{
    [Fact]
    public void HistoryReplay_RewrapsMultilinePromptWithoutLosingTextOrIndent()
    {
        var history = new TerminalHistory();
        history.AddPrompt("甲乙丙丁戊己\r\nsecond line", 24);
        history.TakeDraw(24);

        var replay = history.TakeDraw(8, replay: true);
        var text = replay.Rows.Select(Ansi.StripStyles).Select(row => row.TrimEnd());

        Assert.Equal(["", "› 甲乙丙丁", "  戊己", "  second l", "  ine", ""], text);
        Assert.All(replay.Rows, row => Assert.InRange(Ansi.VisibleLength(row), 0, 10));
        Assert.Empty(history.TakeDraw(8).Rows);
    }
}
