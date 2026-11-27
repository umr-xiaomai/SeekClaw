using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class MarkdownMermaidTests
{
    [Fact]
    public void UnicodeFlowchart_MatchesCodexAndChecksTerminalCells()
    {
        const string source = "%% heading\ngraph LR; A -->|准备| B; A[请求]; B{Réponse?}; B -->|retry| A; B --> C[Ship 🚀]";
        const string expected = """
            ┌──────────────┐  ┌─────────────────────┐  ┌─────────┐
            │ 请求         │  │ ◇ Réponse?          │  │ Ship 🚀 │
            └┬──────┬──────┘  └┬──────┬──────┬──────┘  └┬────────┘
             │      ▲          ▲      │      │          ▲
             │准备  │          │      │retry │          │
             │      │          │      │      │          │
             │      │          │      │      │          │
             └──────╪──────────┘      │      │          │
                    │                 │      │          │
                    └─────────────────┘      │          │
                                             │          │
                                             └──────────┘
            """;
        var width = expected.Split('\n').Max(TextWidth.Of);

        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, width, out var rows));
        Assert.Equal(expected, Join(rows));
        Assert.Equal(MarkdownMermaid.Status.TooWide, MarkdownMermaid.Render(source, width - 1, out var fallback));
        Assert.Empty(fallback);
    }

    [Fact]
    public void BranchMergeAndRetry_MatchCodexWithoutMergingCrossingEdges()
    {
        const string source = """
            flowchart TD
            A["Go []; &"] --> B{"x < 3?"}
            B -->|"y|n"| C[Reserve]
            B -->|no| D[Waitlist]
            C --> E{Paid?}
            E -->|yes| F[Ship]
            E -->|no| G[Retry payment]
            G --> E
            D --> H[Notify buyer]
            F --> H
            """;
        const string expected = """
            ┌───────────────┐
            │ Go []; &      │
            │               ├────────┐
            └───────────────┘        │
                                     │
                                     │
            ┌───────────────┐        │
            │ ◇ x < 3?      │        │
            │               ├◄───────┘
            │               ├──y|n─────┐
            │               ├──no──────╪─┐
            └───────────────┘          │ │
                                       │ │
                                       │ │
            ┌───────────────┐          │ │
            │ Reserve       │          │ │
            │               ├◄─────────┘ │
            │               ├────────────╪─┐
            └───────────────┘            │ │
                                         │ │
                                         │ │
            ┌───────────────┐            │ │
            │ Waitlist      │            │ │
            │               ├◄───────────┘ │
            │               ├──────────────╪───────┐
            └───────────────┘              │       │
                                           │       │
                                           │       │
            ┌───────────────┐              │       │
            │ ◇ Paid?       │              │       │
            │               ├◄─────────────┘       │
            │               ├──yes───────────┐     │
            │               ├──no────────────╪─┐   │
            │               ├◄───────────────╪─╪─┐ │
            └───────────────┘                │ │ │ │
                                             │ │ │ │
                                             │ │ │ │
            ┌───────────────┐                │ │ │ │
            │ Ship          │                │ │ │ │
            │               ├◄───────────────┘ │ │ │
            │               ├──────────────────╪─╪─╪─┐
            └───────────────┘                  │ │ │ │
                                               │ │ │ │
                                               │ │ │ │
            ┌───────────────┐                  │ │ │ │
            │ Retry payment │                  │ │ │ │
            │               ├◄─────────────────┘ │ │ │
            │               ├────────────────────┘ │ │
            └───────────────┘                      │ │
                                                   │ │
                                                   │ │
            ┌───────────────┐                      │ │
            │ Notify buyer  │                      │ │
            │               ├◄─────────────────────┘ │
            │               ├◄───────────────────────┘
            └───────────────┘
            """;

        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 100, out var rows));
        Assert.Equal(expected, Join(rows));
    }

    [Fact]
    public void FlatSubgraphs_MatchCodexAndKeepCrossGroupEdgesOutsideTheFrames()
    {
        const string body = """
            Z --> A
            subgraph Old["Old approach"]
                A[Input] --> B[Output]
            end
            subgraph New["New approach"]
                C[Input] --> D[Output]
            end
            B -->|compare| C
            """;
        const string expected = """
              ┌────────┐
              │ Z      │
              │        ├───────────────────┐
              └────────┘                   │
                                           │
                                           │
            ┌─Old approach──────────────┐  │
            │                           │  │
            │ ┌────────┐                │  │
            │ │ Input  │                │  │
            │ │        ├◄───────────────┼──┘
            │ │        ├────────────┐   │
            │ └────────┘            │   │
            │                       │   │
            │                       │   │
            │ ┌────────┐            │   │
            │ │ Output │            │   │
            │ │        ├◄───────────┘   │
            │ │        ├──compare───────┼────┐
            │ └────────┘                │    │
            │                           │    │
            └───────────────────────────┘    │
                                             │
                                             │
            ┌─New approach──────────────┐    │
            │                           │    │
            │ ┌────────┐                │    │
            │ │ Input  │                │    │
            │ │        ├──────────────┐ │    │
            │ │        ├◄─────────────╪─┼────┘
            │ └────────┘              │ │
            │                         │ │
            │                         │ │
            │ ┌────────┐              │ │
            │ │ Output │              │ │
            │ │        ├◄─────────────┘ │
            │ └────────┘                │
            │                           │
            └───────────────────────────┘
            """;

        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render("flowchart TD\n" + body, 200, out var rows));
        Assert.Equal(expected, Join(rows));
        foreach (var direction in new[] { "BT", "LR", "RL" })
        {
            var source = $"flowchart {direction}\n{body}";
            Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 200, out rows));
            var output = Join(rows);
            var width = output.Split('\n').Max(TextWidth.Of);
            Assert.Contains("Old approach", output);
            Assert.Contains("New approach", output);
            Assert.Contains("compare", output);
            Assert.Equal(3, output.Count(symbol => symbol == '┼'));
            Assert.Equal(MarkdownMermaid.Status.TooWide, MarkdownMermaid.Render(source, width - 1, out _));
        }
    }

    [Theory]
    [InlineData("TD", "Ready", "Done", '◄')]
    [InlineData("TB", "Ready", "Done", '◄')]
    [InlineData("BT", "Done", "Ready", '◄')]
    [InlineData("LR", "Ready", "Done", '▲')]
    [InlineData("RL", "Done", "Ready", '▲')]
    public void ChainsAndGroupedTargets_UseDirectionAndDistinctArrowPorts(string direction, string first, string last, char tip)
    {
        var source = $"graph {direction}; A([Ready]) --> B[Working] & C[Queued] --> D{{Done}}";
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 100, out var rows));
        var output = Join(rows);
        Assert.Contains('╭', output);
        Assert.Contains("◇ Done", output);
        Assert.Equal(4, output.Count(ch => ch == tip));
        Assert.True(output.IndexOf(first, StringComparison.Ordinal) < output.IndexOf(last, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sequenceDiagram\nA->>B: hello\nactivate B")]
    [InlineData("graph; end")]
    [InlineData("graph; subgraph Loop; A")]
    [InlineData("graph; subgraph Loop; end")]
    [InlineData("graph; subgraph Loop; subgraph Retry; A; end; end")]
    [InlineData("graph; subgraph Loop; A; end; subgraph Loop; B; end")]
    [InlineData("graph; subgraph Loop; A; end; Loop --> A")]
    [InlineData("graph LR; A --> B[Incomplete")]
    [InlineData("graph LR; A -->|bad| B; classDef foo fill:red")]
    [InlineData("graph LR; A --> B[&#9731;]")]
    [InlineData("graph LR; A --> B[\u001b[2J]")]
    [InlineData("graph LR; A --> B[\u202eright to left override]")]
    public void UnsupportedSyntaxFallsBackWithoutLeakingAPartialDiagram(string source)
    {
        Assert.Equal(MarkdownMermaid.Status.Unsupported, MarkdownMermaid.Render(source, 100, out var rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void SourceGraphAndCanvasLimits_FallBackBeforePainting()
    {
        var diagrams = new[]
        {
            "graph LR; A[" + new string('汉', 16 * 1024 / 3) + "]",
            "graph LR; " + string.Join(" --> ", Enumerable.Range(0, 17).Select(index => "A" + index)),
            "graph LR; " + string.Concat(Enumerable.Repeat("A --> B; ", 25)),
            "graph LR; A[" + new string('宽', 21) + "]",
            "graph LR; " + string.Concat(Enumerable.Repeat("A -->|" + new string('x', 40) + "| B; ", 24))
        };
        foreach (var diagram in diagrams)
        {
            Assert.Equal(MarkdownMermaid.Status.Limit, MarkdownMermaid.Render(diagram, int.MaxValue, out var rows));
            Assert.Empty(rows);
        }
    }

    [Fact]
    public void StreamingFence_RetainsSourceUntilTheClosingMarkerAndReflowsOnResize()
    {
        const string source = "Heading\n\n```mermaid\nflowchart LR\nA[The opening step] --> B[The final step]\n";
        var stream = new MarkdownStreamRenderer();
        var active = stream.RenderAll(source, 80).Select(Ansi.StripStyles).ToArray();
        Assert.Contains("A[The opening step] --> B[The final step]", active);
        Assert.DoesNotContain(active, line => line.Contains('┌'));
        Assert.Equal(1, stream.SpillableRows);

        var complete = source + "```\n";
        var wide = stream.RenderAll(complete, 80).Select(Ansi.StripStyles).ToArray();
        Assert.Contains(wide, line => line.Contains('┌'));
        Assert.DoesNotContain(wide, line => line.Contains("flowchart LR", StringComparison.Ordinal));
        Assert.Equal(wide, MarkdownAnsi.Render(complete, 80).Select(Ansi.StripStyles));

        var narrow = stream.RenderAll(complete, 24).Select(Ansi.StripStyles).ToArray();
        Assert.Contains("This Mermaid diagram doesn't fit the current terminal width.", string.Join(' ', narrow));
        Assert.Contains("flowchart LR", narrow);
        Assert.DoesNotContain(narrow, line => line.Contains('┌'));
    }

    [Fact]
    public void UnsupportedCompletedFence_PreservesTheSourceWithTheReason()
    {
        const string source = "```mermaid\nsequenceDiagram\nA->>B: hello\nactivate B\n```";
        Assert.Equal(
            ["This Mermaid diagram uses features the terminal renderer doesn't support.", "sequenceDiagram", "A->>B: hello", "activate B"],
            MarkdownAnsi.Render(source, 80).Select(Ansi.StripStyles));
    }

    private static string Join(IEnumerable<MarkdownLayout.Run[]> rows) =>
        string.Join('\n', rows.Select(row => string.Concat(row.Select(run => run.Text))));
}
