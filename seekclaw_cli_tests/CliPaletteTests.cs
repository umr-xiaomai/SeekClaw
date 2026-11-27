using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class CliPaletteTests
{
    [Fact]
    public void StreamingMarkdown_UsesTheNewPaletteEvenWhenCachedSourceHasNotChanged()
    {
        const string source = """
            # Status

            | Check | Result |
            | --- | --- |
            | API | Ready |

            ```csharp
            return 42;
            ```

            ```mermaid
            graph LR; A --> B
            ```
            """ + "\n";
        var stream = new MarkdownStreamRenderer();
        var light = stream.RenderAll(source, 64, palette: CliPalette.Light);
        var dark = stream.RenderAll(source, 64, palette: CliPalette.Dark);

        Assert.Equal(light.Select(Ansi.StripStyles), dark.Select(Ansi.StripStyles));
        Assert.Equal(MarkdownAnsi.Render(source, 64, workingDirectory: null, CliPalette.Dark), dark);
        foreach (var (rows, palette) in new[] { (light, CliPalette.Light), (dark, CliPalette.Dark) })
        {
            Assert.Contains(rows, row => row.Contains(palette.Heading, StringComparison.Ordinal));
            Assert.Contains(rows, row => row.Contains(palette.TableHeader, StringComparison.Ordinal));
            Assert.Contains(rows, row => row.Contains(palette.CodeKeyword, StringComparison.Ordinal));
            Assert.Contains(rows, row => row.Contains(palette.CodeVariable, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ExplicitThemes_PaintOwnedRowsAndKeepIncrementalOutputIncremental()
    {
        var size = new TerminalSize(28, 12);
        using var output = new StringWriter();
        var region = new LiveRegion(output);

        region.Update(["Committed"], ["Live"], size, palette: CliPalette.Light);
        var initial = output.ToString();
        Assert.Contains(CliPalette.Light.Text, initial);
        Assert.Contains("\x1b[48;2;", initial);
        Assert.Contains("\x1b[K" + Ansi.Reset, initial);
        Assert.DoesNotContain("\x1b]", initial); // The terminal's own palette is never set.

        output.GetStringBuilder().Clear();
        region.Update(null, ["Live text"], size, palette: CliPalette.Light);
        Assert.Equal(" text", Ansi.StripControls(output.ToString()).Trim('\n'));

        output.GetStringBuilder().Clear();
        region.Update(null, ["Live text"], size, palette: CliPalette.Dark);
        var changed = output.ToString();
        Assert.Contains(CliPalette.Dark.Text, changed);
        Assert.DoesNotContain("\x1b[3J", changed);
        Assert.Equal("Live text", Ansi.StripControls(changed).Trim('\n'));

        output.GetStringBuilder().Clear();
        region.Update(["Live text"], [], size, palette: CliPalette.Dark);
        Assert.DoesNotContain("Live text", output.ToString());
    }

    [Fact]
    public void ThemeChange_DoesNotReplayThePrefixAlreadyEmittedDuringAStream()
    {
        const string source = "## Status\n\nDetails";
        var history = new TerminalHistory();
        var light = AssistantCell.Render(source, 40, palette: CliPalette.Light);
        history.ShowDraft(light, availableRows: 2, spillableRows: light.Count, palette: CliPalette.Light);
        Assert.NotEmpty(history.TakeDraw(40).Rows);

        var dark = AssistantCell.Render(source, 40, palette: CliPalette.Dark);
        history.ShowDraft(dark, availableRows: 2, spillableRows: dark.Count, palette: CliPalette.Dark);
        var change = history.TakeDraw(40);
        Assert.False(change.Replay);
        Assert.Empty(change.Rows);
    }
}
