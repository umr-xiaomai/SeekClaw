using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class MarkdownMermaidSequenceTests
{
    [Fact]
    public void NestedSequence_MatchesCodexAndFitsExactlyAtItsCellWidth()
    {
        var expected = Expected.ReplaceLineEndings("\n");
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(Sequence, 200, out var rows));
        Assert.Equal(expected, Join(rows));

        var width = expected.Split('\n').Max(TextWidth.Of);
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(Sequence, width, out rows));
        Assert.Equal(expected, Join(rows));
        Assert.Equal(MarkdownMermaid.Status.TooWide, MarkdownMermaid.Render(Sequence, width - 1, out rows));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("->>", '▶', '◀', false)]
    [InlineData("-->>", '▶', '◀', true)]
    [InlineData("->", '─', '─', false)]
    [InlineData("-->", '┄', '┄', true)]
    [InlineData("-x", 'x', 'x', false)]
    [InlineData("--x", 'x', 'x', true)]
    public void Arrows_PreserveRecipientAndStyleInBothDirectionsAndSelfMessages(
        string arrow, char forward, char reverse, bool dashed)
    {
        foreach (var (sender, recipient, tip) in new[] { ('A', 'B', forward), ('B', 'A', reverse), ('B', 'B', reverse) })
        {
            var source = $"sequenceDiagram; participant A; participant B; {sender}{arrow}{recipient}: msg";
            Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 100, out var rows));
            var lines = Join(rows).Split('\n');
            var recipientColumn = lines[1].IndexOf(recipient);
            var stroke = lines[sender == recipient ? 5 : 4];
            Assert.Equal(tip, stroke[recipientColumn]);
            Assert.Equal(dashed, stroke.Contains('┄'));
        }
    }

    [Fact]
    public void QuotedMessageText_PreservesQuotesAndStillSplitsStatements()
    {
        const string source = "sequenceDiagram; A->>B: say \"hello; B->>A: world\"";
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 180, out var rows));
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source.Replace(';', '\n'), 180, out var split));
        Assert.Equal(Join(rows), Join(split));
        Assert.Contains("say \"hello", Join(rows));
        Assert.Contains("world\"", Join(rows));
    }

    [Theory]
    [InlineData("sequenceDiagram; participant A as x; participant A as y")]
    [InlineData("sequenceDiagram; alt ready; A->>B: hi")]
    [InlineData("sequenceDiagram; A->>B: hi; end")]
    [InlineData("sequenceDiagram; loop retry; else no; end")]
    [InlineData("sequenceDiagram; alt one; else two; else three; end")]
    [InlineData("sequenceDiagram; A->>B: <br/>")]
    [InlineData("sequenceDiagram; A->>B: \u001b[31m")]
    [InlineData("sequenceDiagram; participant A as e\u0301")]
    public void UnsupportedSequences_ReturnNoPartialDiagram(string source)
    {
        Assert.Equal(MarkdownMermaid.Status.Unsupported, MarkdownMermaid.Render(source, 200, out var rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void SequenceLimits_ReturnNoPartialDiagramEvenWithUnlimitedWidth()
    {
        var diagrams = new[]
        {
            "sequenceDiagram; " + string.Join("; ", Enumerable.Range(0, 9).Select(index => $"participant P{index}")),
            "sequenceDiagram; " + string.Concat(Enumerable.Repeat("A->>B: msg; ", 65)),
            "sequenceDiagram; " + string.Concat(Enumerable.Repeat("loop retry; ", 5)) +
                "A->>B: msg; " + string.Concat(Enumerable.Repeat("end; ", 5)),
            "sequenceDiagram; " + string.Join("; ", Enumerable.Range(0, 8).Select(index => $"participant P{index}")) +
                "; " + string.Concat(Enumerable.Repeat($"P0->>P7: {new string('x', 40)}; ", 64))
        };
        foreach (var source in diagrams)
        {
            Assert.Equal(MarkdownMermaid.Status.Limit, MarkdownMermaid.Render(source, int.MaxValue, out var rows));
            Assert.Empty(rows);
        }
    }

    private static string Join(IEnumerable<MarkdownLayout.Run[]> rows) =>
        string.Join('\n', rows.Select(row => string.Concat(row.Select(run => run.Text))));

    // Fixture and expected layout from Codex codex-rs/mermaid, revision af81d2770f (Apache-2.0).
    private const string Sequence = """
        sequenceDiagram
            actor U as Buyer
            participant A as API
            participant S as 库存
            participant P as Payments
            U->>A: data[0] = {x: 1}
            A->>S: Reserve items
            S-->>A: say "hello" | C:\tmp
            opt Items reserved
                loop Up to 3 attempts
                    A->>P: Charge card
                    P->>P: Check fraud
                    P-->>A: Payment status
                    alt Approved
                        Note over A,P: Payment recorded
                        A-->>U: Order confirmed
                    else Declined
                        A->>S: Release items
                        A-->>U: Payment failed
                    end
                end
            end
        """;

    private const string Expected = """
                  ┌───────────────┐         ┌───────────────┐         ┌───────────────┐         ┌───────────────┐
                  │ Buyer (actor) │         │ API           │         │ 库存          │         │ Payments      │
                  └───────────────┘         └───────────────┘         └───────────────┘         └───────────────┘
                          │ data[0] = {x: 1}        │                         │                         │
                          ├─────────────────────────▶                         │                         │
                          │                         │                         │                         │
                          │                         │ Reserve items           │                         │
                          │                         ├─────────────────────────▶                         │
                          │                         │                         │                         │
                          │                         │ say "hello" | C:\tmp    │                         │
                          │                         ◀┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┤                         │
                          │                         │                         │                         │
        ┌─opt Items reserved───────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
        │                 │                         │                         │                         │                                  │
        │                 │                         │                         │                         │                                  │
        │ ┌─loop Up to 3 attempts────────────────────────────────────────────────────────────────────────────────────────────────────────┐ │
        │ │               │                         │                         │                         │                                │ │
        │ │               │                         │                         │                         │                                │ │
        │ │               │                         │ Charge card             │                         │                                │ │
        │ │               │                         ├─────────────────────────┼─────────────────────────▶                                │ │
        │ │               │                         │                         │                         │                                │ │
        │ │               │                         │                         │                         │ Check fraud                    │ │
        │ │               │                         │                         │                         ├───┐                            │ │
        │ │               │                         │                         │                         ◀───┘                            │ │
        │ │               │                         │                         │                         │                                │ │
        │ │               │                         │ Payment status          │                         │                                │ │
        │ │               │                         ◀┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┼┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┤                                │ │
        │ │               │                         │                         │                         │                                │ │
        │ │ ┌─alt Approved─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ │             │                         ┌───────────────────────────────────────────────────┐                              │ │ │
        │ │ │             │                         │ Note: Payment recorded                            │                              │ │ │
        │ │ │             │                         └───────────────────────────────────────────────────┘                              │ │ │
        │ │ │             │ Order confirmed         │                         │                         │                              │ │ │
        │ │ │             ◀┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┤                         │                         │                              │ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ ├─else Declined────────────────────────────────────────────────────────────────────────────────────────────────────────────┤ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ │             │                         │ Release items           │                         │                              │ │ │
        │ │ │             │                         ├─────────────────────────▶                         │                              │ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ │             │ Payment failed          │                         │                         │                              │ │ │
        │ │ │             ◀┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┤                         │                         │                              │ │ │
        │ │ │             │                         │                         │                         │                              │ │ │
        │ │ └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ │ │
        │ │               │                         │                         │                         │                                │ │
        │ │               │                         │                         │                         │                                │ │
        │ └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘ │
        │                 │                         │                         │                         │                                  │
        │                 │                         │                         │                         │                                  │
        └──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                          │                         │                         │                         │
                          │                         │                         │                         │
        """;
}
