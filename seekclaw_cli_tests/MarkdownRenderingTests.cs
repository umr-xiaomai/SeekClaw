using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class MarkdownRenderingTests
{
    [Fact]
    public void Transcript_MatchesCodexBlockSpacingAndTableLayout()
    {
        const string source = """
            # Heading

            First paragraph with a [link](https://example.com).
            continued on the next line.

            1. First item
            2. Second item

            > Quoted paragraph

            ```rust
            fn main() {}
            ```

            | Key | Value |
            | --- | --- |
            | alpha | beta |
            """;

        Assert.Equal(
            ["# Heading", "", "First paragraph with a link (https://example.com).", "continued on the next line.", "",
             "1. First item", "2. Second item", "", "> Quoted paragraph", "", "fn main() {}", "",
             " Key      Value", "━━━━━━━  ━━━━━━━", " alpha    beta"],
            MarkdownAnsi.Render(source, 76).Select(Ansi.StripStyles));
    }

    [Fact]
    public void Transcript_WrapsListsAndQuotesWithUnicodeColumnWidths()
    {
        const string source = """
            - [ ] 中文 👩‍💻 校对
                - [x] Nested
            - Closed

            > 窗口宽度会换行

            ![image description](https://example.com/image.png)
            """;

        var lines = MarkdownAnsi.Render(source, 15).Select(Ansi.StripStyles).ToArray();
        Assert.Equal(["☐ 中文 👩‍💻 校对", "    ☑ Nested", "", "• Closed", "", "> 窗口宽度会换", "> 行", "", "image", "description"], lines);
        Assert.All(lines, line => Assert.InRange(TextWidth.Of(line), 0, 15));
    }

    [Fact]
    public void MarkdownFence_ShowsACompletedTableAndPreservesOtherExamples()
    {
        const string unfinishedRow = "```markdown\n| Key | Value |\n| --- | --- |\n| alpha | beta |";
        const string opening = unfinishedRow + "\n";
        const string complete = opening + "```";
        string[] expected = [" Key      Value", "━━━━━━━  ━━━━━━━", " alpha    beta"];
        var stream = new MarkdownStreamRenderer();

        Assert.DoesNotContain("| alpha | beta |", stream.RenderAll(unfinishedRow, 40).Select(Ansi.StripStyles));
        Assert.Contains("| alpha | beta |", stream.RenderAll(opening, 40).Select(Ansi.StripStyles));
        Assert.Equal(expected, stream.RenderAll(complete + "\n", 40).Select(Ansi.StripStyles));
        Assert.Equal(expected, MarkdownAnsi.Render(complete, 40).Select(Ansi.StripStyles));
        Assert.Equal(["# Markdown example"], MarkdownAnsi.Render("```md\n# Markdown example\n```", 40).Select(Ansi.StripStyles));
    }

    [Fact]
    public void NarrowTable_PreservesEveryValueWithoutOverflowing()
    {
        const string source = "| Property | Explanation | Result |\n| --- | --- | --- |\n| Version | A long description | Ready |";
        var lines = MarkdownAnsi.Render(source, 16).Select(Ansi.StripStyles).ToArray();

        Assert.Equal([" Property", "  Version", " Explanation", "  A long", "  description", " Result", "  Ready"], lines);
        Assert.All(lines, line => Assert.True(TextWidth.Of(line) <= 16));
    }

    [Fact]
    public void FormulaRendering_PreservesShellCurrencyAndUnsupportedMath()
    {
        const string source = """
            Inline $\alpha^2 + \beta_{10}$ on $\mathbb{R}^n$.
            Shell: $HOME. Cost: $5 and $10. Code: `$\alpha$`.
            Unsupported: $\unknown{x}$.

            $$
            x=\frac{12}{3}
            $$
            """;

        Assert.Equal(["Inline α² + β₁₀ on ℝⁿ.", "Shell: $HOME. Cost: $5 and $10. Code: $\\alpha$.",
            "Unsupported: $\\unknown{x}$.", "", "  12", "x=──", "  3"],
            MarkdownAnsi.Render(source, 80).Select(Ansi.StripStyles));
    }

    [Theory]
    [InlineData("https://example.com/a/very/long/path/to/the/guide")]
    [InlineData("https://example.com/a_(nested_(path))")]
    [InlineData(@"https://example.com/escaped\)parenthesis")]
    [InlineData(" <https://example.com/unbalanced)parenthesis>")]
    [InlineData("https://example.com/guide \"a title with ) parentheses\"")]
    [InlineData("https://example.com/guide (x < y)")]
    public void Stream_KeepsTheLabelVisibleUntilTheLinkDestinationCloses(string destination)
    {
        const int width = 24;
        const string label = "See [documentation]";
        var stream = new MarkdownStreamRenderer();
        var labelRows = stream.RenderAll(label, width);
        var unfinished = "(" + destination;

        for (var end = 1; end <= unfinished.Length; end++)
            Assert.Equal(labelRows, stream.RenderAll(label + unfinished[..end], width));

        var completed = label + unfinished + ") and continue.";
        Assert.Equal(MarkdownAnsi.Render(completed, width), stream.RenderAll(completed, width));
    }

    [Theory]
    [InlineData("Run ``[command](argument")]
    [InlineData(@"See \[item](description")]
    public void Stream_PreservesLinkLikeTextInCodeOrAfterAnEscape(string source)
    {
        var stream = new MarkdownStreamRenderer();
        Assert.Equal(MarkdownAnsi.Render(source, 40), stream.RenderAll(source, 40));
    }

    [Theory]
    [InlineData('`')]
    [InlineData('~')]
    public void Stream_HoldsTentativeFenceMarkersUntilThereIsACompleteLine(char marker)
    {
        const string prefix = "Before\n\n";
        var stream = new MarkdownStreamRenderer();
        var before = stream.RenderAll(prefix, 40);

        for (var count = 1; count <= 3; count++)
            Assert.Equal(before, stream.RenderAll(prefix + new string(marker, count), 40));

        var fence = new string(marker, 3);
        var completed = prefix + fence + "text\nInside\n" + fence + "\n";
        Assert.Equal(["Before", "", "Inside"], stream.RenderAll(completed, 40).Select(Ansi.StripStyles));
    }

    [Fact]
    public void Stream_ReusesNormalizedBlocksAndResolvesReferencesAddedLater()
    {
        var stream = new MarkdownStreamRenderer();
        const string prefix = "\x1b[2J# Update\r\n\r\n[guide][doc]\r\n\r\nSome partial";
        const string suffix = " text\r\n\r\n[doc]: https://example.com/guide\r\n\r\nFinal paragraph";

        Assert.Equal(MarkdownAnsi.Render(prefix, 34), stream.RenderAll(prefix, 34));
        var complete = stream.RenderAll(prefix + suffix, 34);
        Assert.Equal(MarkdownAnsi.Render(prefix + suffix, 34), complete);
        Assert.Contains("https://example.com/guide", string.Join(' ', complete.Select(Ansi.StripStyles)));
        Assert.DoesNotContain("\x1b[2J", string.Join('\n', complete));

        var resized = stream.RenderAll(prefix + suffix, 18);
        Assert.Equal(MarkdownAnsi.Render(prefix + suffix, 18), resized);
        stream.Reset();
        Assert.Equal(["new response"], stream.RenderAll("new response", 18));
    }
    [Fact]
    public void SourceReferences_UseTheSessionDirectoryAndPreserveLineLocations()
    {
        const string source = "[Read this](<file:///C:/Repo/Src/My%20File.cs#L18C3-L20C9>)\n\n" +
            "[](<C:/Repository/Other.cs:5>)\n\n[README](README.md:42)";
        var lines = MarkdownAnsi.Render(source, 80, "c:/repo").Select(Ansi.StripStyles);

        Assert.Equal(["Read this (Src/My File.cs:18:3-20:9)", "", "C:/Repository/Other.cs:5", "", "README (README.md:42)"], lines);
        Assert.Equal("//server/share/My File.cs:3", MarkdownLinks.LocalTarget("file://server/share/My%20File.cs#L3", "/repo"));
        Assert.Equal("lib.rs:12", MarkdownLinks.LocalTarget("/repo/lib.rs#L12", "/repo"));
    }

    [Fact]
    public void WebLinks_RejectControlCharactersAndKeepFallbackDestinations()
    {
        Assert.Null(MarkdownLinks.WebDestination("javascript:alert(1)"));
        Assert.Null(MarkdownLinks.WebDestination("file:///tmp/source.cs"));
        Assert.Null(MarkdownLinks.WebDestination("https://example.com/\x1b]0;title\x07"));
        Assert.False(MarkdownLinks.IsSafeSequence(MarkdownLinks.OpenPrefix + "data:text/plain,bad" + MarkdownLinks.Terminator));
        Assert.True(MarkdownLinks.SupportsHyperlinks(name => name == "WT_SESSION" ? "host" : null));
        Assert.False(MarkdownLinks.SupportsHyperlinks(name => name is "WT_SESSION" or "TMUX" ? "host" : null));

        var bareLink = MarkdownAnsi.Render("Read https://example.com/guide or www.example.org/doc.", 80);
        Assert.Equal(["Read https://example.com/guide or www.example.org/doc."], bareLink.Select(Ansi.StripControls));
        Assert.DoesNotContain("\x1b]8;;", string.Concat(MarkdownAnsi.Render("[bad](javascript:alert(1))", 80)));
    }

    [Fact]
    public void WrappedLinks_ReopenTheSameDestinationAndKeepStyledGraphemesIntact()
    {
        const string web = "https://example.com/guide";
        var rows = MarkdownLayout.Wrap([
            new("👩", Ansi.Bold, web), new("‍💻abc", Ansi.Italic, web), new(" tail")
        ], 4);
        var opening = MarkdownLinks.OpenPrefix + web + MarkdownLinks.Terminator;

        Assert.Equal(["👩‍💻ab", "c", "tail"], rows.Select(Ansi.StripControls));
        Assert.All(rows.Take(2), row =>
        {
            Assert.StartsWith(opening, row);
            Assert.EndsWith(MarkdownLinks.Close + Ansi.Reset, row);
        });
        Assert.DoesNotContain(MarkdownLinks.OpenPrefix, rows[2]);
    }

    [Fact]
    public void LiveMarkdownLinks_KeepTheirWidthAndCommitWithoutReprintingTheRow()
    {
        const string destination = "https://example.com/a-long-guide-name";
        var row = Assert.Single(MarkdownLayout.Wrap([new("查看指南", Ansi.Bold, destination), new(" now")], 17));
        var size = new TerminalSize(18, 12);
        using var output = new StringWriter();
        var region = new LiveRegion(output);

        region.Update(null, [row], size);
        Assert.Contains(row, output.ToString());
        Assert.Equal(12, Ansi.VisibleLength(row));
        Assert.Equal("查看指南 now", Ansi.StripStyles(row));

        output.GetStringBuilder().Clear();
        region.Update([row], [], size);
        Assert.DoesNotContain(destination, output.ToString());

        output.GetStringBuilder().Clear();
        region.Update([row], [], size, replaceHistory: true);
        Assert.Contains(row, output.ToString());
    }

    [Fact]
    public void Truncation_KeepsStyledGraphemesAndClosesTheLinkBeforeTheEllipsis()
    {
        var opening = MarkdownLinks.OpenPrefix + "https://example.com/guide" + MarkdownLinks.Terminator;
        var row = opening + Ansi.Bold + "👩" + Ansi.Reset + "‍💻e" + Ansi.Italic + "\u0301abc" +
            MarkdownLinks.Close + Ansi.Reset;
        var clipped = Ansi.TruncateVisible(row, 4);

        Assert.StartsWith(opening, clipped);
        Assert.EndsWith(MarkdownLinks.Close + Ansi.Reset + "…", clipped);
        Assert.Equal("👩‍💻e\u0301…", Ansi.StripStyles(clipped));
        Assert.Equal(4, Ansi.VisibleLength(clipped));
    }

    [Fact]
    public void PlainHistory_RemovesRawTerminalLinksAndKeepsTheApplicationStyle()
    {
        var opening = MarkdownLinks.OpenPrefix + "https://example.com/guide" + MarkdownLinks.Terminator;
        var notice = Ansi.Yellow + opening + "warning" + MarkdownLinks.Close + "\x1b[2J" + Ansi.Reset;
        var history = new TerminalHistory();
        history.Add(notice);

        Assert.Equal(["warning".Style(Ansi.Yellow)], history.TakeDraw(80).Rows);
        Assert.Equal("warning", Ansi.StripControls(notice));
    }

    [Fact]
    public void CrampedTables_ChangeToRecordsAndKeepTheLabels()
    {
        const string source = """
            | Key | Notes |
            | --- | --- |
            | firstlongid | A readable explanatory sentence for this row. |
            | secondlongid | Another readable explanatory sentence for this row. |
            | short | A final readable explanatory sentence. |
            """;

        var narrow = MarkdownAnsi.Render(source, 17).Select(Ansi.StripStyles).ToArray();
        Assert.Equal([" Key", "  firstlongid", " Notes"], narrow.Take(3));
        Assert.Contains(new string('─', 17), narrow);
        Assert.Contains("  secondlongid", narrow);
        Assert.All(narrow, line => Assert.True(TextWidth.Of(line) <= 17));
        Assert.Contains(MarkdownAnsi.Render(source, 72), line => line.Contains('━'));
    }

    [Fact]
    public void HeaderOnlyTable_FallsBackToPipesWithoutDetachingALink()
    {
        const string source = "| [Guide](./guide.md) | A | B | C | D | E |\n| --- | --- | --- | --- | --- | --- |";
        var lines = MarkdownAnsi.Render(source, 32).Select(Ansi.StripStyles).ToArray();

        Assert.StartsWith("| Guide (./guide.md)", lines[0]);
        Assert.Contains(lines, line => line.StartsWith("| --- |", StringComparison.Ordinal));
    }
}
