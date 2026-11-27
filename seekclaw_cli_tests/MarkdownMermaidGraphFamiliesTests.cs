using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

/// <summary>Snapshot fixtures adapted from codex-rs/mermaid (Apache-2.0).</summary>
public sealed class MarkdownMermaidGraphFamiliesTests
{
    [Theory]
    [InlineData(State, StateExpected)]
    [InlineData(Class, ClassExpected)]
    [InlineData(Entity, EntityExpected)]
    public void GraphFamilies_MatchCodexAndFallbackBeforeWrapping(string source, string expected)
    {
        var width = expected.Split('\n').Max(TextWidth.Of);
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, width, out var rows));
        Assert.Equal(expected.ReplaceLineEndings("\n"), Join(rows));
        Assert.Equal(MarkdownMermaid.Status.TooWide, MarkdownMermaid.Render(source, width - 1, out rows));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("<|--", '◁', '─', false)]
    [InlineData("*--", '◆', '─', false)]
    [InlineData("o--", '◇', '─', false)]
    [InlineData("-->", '─', '◄', false)]
    [InlineData("--", '─', '─', false)]
    [InlineData("..>", '─', '◄', true)]
    [InlineData("..|>", '─', '◁', true)]
    [InlineData("..", '─', '─', true)]
    [InlineData("<--", '◄', '─', false)]
    [InlineData("--*", '─', '◆', false)]
    [InlineData("--o", '─', '◇', false)]
    [InlineData("--|>", '─', '◁', false)]
    public void ClassLinks_PreserveBothEndsAndLineStyle(string token, char sourceTip, char targetTip, bool dashed)
    {
        var source = $"classDiagram; A \"one\" {token} \"many\" B : relates";
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 90, out var rows));
        var text = Join(rows);
        var ports = text.Split('\n').Where(line => line.Contains('├')).ToArray();
        Assert.Equal(2, ports.Length);
        Assert.Contains($"├{sourceTip}", ports[0]);
        Assert.Contains($"├{targetTip}", ports[1]);
        Assert.Contains("(one) relates", ports[0]);
        Assert.Contains("(many)", ports[1]);
        Assert.Equal(dashed, text.Contains('┆'));
    }

    [Theory]
    [InlineData("||", "||", "1", "1")]
    [InlineData("|o", "o|", "0..1", "0..1")]
    [InlineData("}|", "|{", "1..many", "1..many")]
    [InlineData("}o", "o{", "0..many", "0..many")]
    public void EntityLinks_PreserveCardinalityAtBothEnds(string from, string to, string first, string second)
    {
        var source = $"erDiagram; A {from}--{to} B : owns";
        Assert.Equal(MarkdownMermaid.Status.Success, MarkdownMermaid.Render(source, 90, out var rows));
        var ports = Join(rows).Split('\n').Where(line => line.Contains('├')).ToArray();
        Assert.Equal(2, ports.Length);
        Assert.Contains($"({first}) owns", ports[0]);
        Assert.Contains($"({second})", ports[1]);
    }

    [Theory]
    [InlineData("stateDiagram-v2; state Process {; A --> B; }")]
    [InlineData("stateDiagram-v2; accDescr: Order lifecycle; [*] --> Ready")]
    [InlineData("stateDiagram-v2; A --> B:::highlight")]
    [InlineData("classDiagram; class A; A --? B")]
    [InlineData("classDiagram; class A {\n+run();")]
    [InlineData("erDiagram; A ||--o{ B")]
    [InlineData("erDiagram; A ||--|| B: \"owns\"")]
    [InlineData("erDiagram; A {; int x KEY; }")]
    public void UnsupportedOrPartialFamilies_PreserveSourceWithoutAPartialDiagram(string source)
    {
        Assert.Equal(MarkdownMermaid.Status.Unsupported, MarkdownMermaid.Render(source, 180, out var rows));
        Assert.Empty(rows);
    }

    [Fact]
    public void StateAndRelationBodies_EnforceMemberAndEdgeLimits()
    {
        var diagrams = new[]
        {
            "classDiagram; class A {\n" + string.Concat(Enumerable.Repeat("+field\n", 17)) + "}",
            "erDiagram; A {; " + string.Concat(Enumerable.Repeat("int id;", 17)) + " }",
            "stateDiagram-v2; " + string.Concat(Enumerable.Repeat("A --> B;", 25)),
            "stateDiagram-v2\n" + string.Concat(Enumerable.Repeat("S: Description\n", 18))
        };
        foreach (var source in diagrams)
        {
            Assert.Equal(MarkdownMermaid.Status.Limit, MarkdownMermaid.Render(source, int.MaxValue, out var rows));
            Assert.Empty(rows);
        }
    }

    private static string Join(IEnumerable<MarkdownLayout.Run[]> rows) =>
        string.Join('\n', rows.Select(row => string.Concat(row.Select(run => run.Text))));

    private const string State = """
        stateDiagram-v2
            state "Payment pending" as Charging
            [*] --> Draft
            Draft --> Validating: submit
            Validating --> Charging: [ready]
            Validating --> Rejected: invalid
            Charging --> Packing: paid
            Charging --> Rejected: declined
            Packing --> Shipped: dispatch
            Shipped --> Delivered: received
            Delivered --> [*]
            Rejected --> Draft: revise
            Charging: Retry up to 3 times
            Draft: Order drafted
            Draft: Awaiting submission
        """;

    private const string StateExpected = """
        ┌─────────────────────┐
        │ Payment pending     │
        │ ───────────────     │
        │ Retry up to 3 times │
        │                     ├◄────────────────┐
        │                     ├──paid───────────╪───┐
        │                     ├──declined───────╪───╪─┐
        └─────────────────────┘                 │   │ │
                                                │   │ │
                                                │   │ │
        ┌─────────────────────┐                 │   │ │
        │ ● initial           │                 │   │ │
        │                     ├─────────────┐   │   │ │
        └─────────────────────┘             │   │   │ │
                                            │   │   │ │
                                            │   │   │ │
        ┌─────────────────────┐             │   │   │ │
        │ Order drafted       │             │   │   │ │
        │ ─────────────       │             │   │   │ │
        │ Awaiting submission │             │   │   │ │
        │                     ├◄────────────┘   │   │ │
        │                     ├──submit───────┐ │   │ │
        │                     ├◄──────────────╪─╪───╪─╪───────┐
        └─────────────────────┘               │ │   │ │       │
                                              │ │   │ │       │
                                              │ │   │ │       │
        ┌─────────────────────┐               │ │   │ │       │
        │ Validating          │               │ │   │ │       │
        │                     ├◄──────────────┘ │   │ │       │
        │                     ├──[ready]────────┘   │ │       │
        │                     ├──invalid──────────┐ │ │       │
        └─────────────────────┘                   │ │ │       │
                                                  │ │ │       │
                                                  │ │ │       │
        ┌─────────────────────┐                   │ │ │       │
        │ Rejected            │                   │ │ │       │
        │                     ├◄──────────────────┘ │ │       │
        │                     ├◄────────────────────╪─┘       │
        │                     ├──revise─────────────╪─────────┘
        └─────────────────────┘                     │
                                                    │
                                                    │
        ┌─────────────────────┐                     │
        │ Packing             │                     │
        │                     ├◄────────────────────┘
        │                     ├──dispatch───────────────┐
        └─────────────────────┘                         │
                                                        │
                                                        │
        ┌─────────────────────┐                         │
        │ Shipped             │                         │
        │                     ├◄────────────────────────┘
        │                     ├──received─────────────────┐
        └─────────────────────┘                           │
                                                          │
                                                          │
        ┌─────────────────────┐                           │
        │ Delivered           │                           │
        │                     ├◄──────────────────────────┘
        │                     ├─────────────────────────────┐
        └─────────────────────┘                             │
                                                            │
                                                            │
        ┌─────────────────────┐                             │
        │ ◎ final             │                             │
        │                     ├◄────────────────────────────┘
        └─────────────────────┘
        """;

    private const string Class = """
        classDiagram
            class order {
                +int[] ids
                +String a;b
                +submit()
                +cancel()
            }
            class LineItem {
                +int quantity
                +Decimal price
                +subtotal()
            }
            class Payment {
                +Decimal amount
                +authorize()
            }
            class CardPayment {
                +String lastFour
                +authorize()
            }
            order "1" *-- "1..*" LineItem : contains
            order "1" --> "1" Payment : pays with
            Payment <|-- CardPayment
            CardPayment ..order : updates
        """;

    private const string ClassExpected = """
        ┌──────────────────┐
        │ order            │
        │ ─────            │
        │ +int[] ids       │
        │ +String a;b      │
        │ +submit()        │
        │ +cancel()        │
        │                  ├◆─(1) contains────┐
        │                  ├──(1) pays with───╪─┐
        │                  ├─┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄╪┄╪┄┄┄┐
        └──────────────────┘                  │ │   ┆
                                              │ │   ┆
                                              │ │   ┆
        ┌──────────────────┐                  │ │   ┆
        │ LineItem         │                  │ │   ┆
        │ ────────         │                  │ │   ┆
        │ +int quantity    │                  │ │   ┆
        │ +Decimal price   │                  │ │   ┆
        │ +subtotal()      │                  │ │   ┆
        │                  ├──(1..*)──────────┘ │   ┆
        └──────────────────┘                    │   ┆
                                                │   ┆
                                                │   ┆
        ┌──────────────────┐                    │   ┆
        │ Payment          │                    │   ┆
        │ ───────          │                    │   ┆
        │ +Decimal amount  │                    │   ┆
        │ +authorize()     │                    │   ┆
        │                  ├◄─(1)───────────────┘   ┆
        │                  ├◁─────────────────────┐ ┆
        └──────────────────┘                      │ ┆
                                                  │ ┆
                                                  │ ┆
        ┌──────────────────┐                      │ ┆
        │ CardPayment      │                      │ ┆
        │ ───────────      │                      │ ┆
        │ +String lastFour │                      │ ┆
        │ +authorize()     │                      │ ┆
        │                  ├──────────────────────┘ ┆
        │                  ├─┄updates┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┘
        └──────────────────┘
        """;

    private const string Entity = """
        erDiagram
            CUSTOMER ||--o{ ORDER : places
            ORDER ||--|{ LINE_ITEM : contains
            PRODUCT ||..o{ LINE_ITEM : appears_in
            CUSTOMER {
                int id PK
                string email UK
                string name
            }
            ORDER {
                int id PK
                int customer_id FK
                string status
            }
            LINE_ITEM {
                int order_id PK, FK "order key"
                int product_id PK, FK
                int quantity
            }
            PRODUCT {
                int id PK
                string name
                decimal price
            }
        """;

    private const string EntityExpected = """
        ┌─────────────────────────────────┐
        │ CUSTOMER                        │
        │ ────────                        │
        │ int id PK                       │
        │ string email UK                 │
        │ string name                     │
        │                                 ├──(1) places───────┐
        └─────────────────────────────────┘                   │
                                                              │
                                                              │
        ┌─────────────────────────────────┐                   │
        │ ORDER                           │                   │
        │ ─────                           │                   │
        │ int id PK                       │                   │
        │ int customer_id FK              │                   │
        │ string status                   │                   │
        │                                 ├──(0..many)────────┘
        │                                 ├──(1) contains───────┐
        └─────────────────────────────────┘                     │
                                                                │
                                                                │
        ┌─────────────────────────────────┐                     │
        │ LINE_ITEM                       │                     │
        │ ─────────                       │                     │
        │ int order_id PK, FK — order key │                     │
        │ int product_id PK, FK           │                     │
        │ int quantity                    │                     │
        │                                 ├──(1..many)──────────┘
        │                                 ├─┄(0..many)┄┄┄┄┄┄┄┄┄┄┄┄┐
        └─────────────────────────────────┘                       ┆
                                                                  ┆
                                                                  ┆
        ┌─────────────────────────────────┐                       ┆
        │ PRODUCT                         │                       ┆
        │ ───────                         │                       ┆
        │ int id PK                       │                       ┆
        │ string name                     │                       ┆
        │ decimal price                   │                       ┆
        │                                 ├─┄(1) appears_in┄┄┄┄┄┄┄┘
        └─────────────────────────────────┘
        """;
}
