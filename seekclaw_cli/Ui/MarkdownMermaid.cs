using System.Globalization;
using System.Text;
using static SeekClaw.Cli.Ui.MarkdownLayout;

namespace SeekClaw.Cli.Ui;

/// <summary>Renders the supported Mermaid flowchart grammar in terminal cells.</summary>
/// <remarks>Adapted from OpenAI Codex codex-rs/mermaid (Apache-2.0, revision af81d2770f).</remarks>
internal static partial class MarkdownMermaid
{
    private const int MaxSourceBytes = 16 * 1024;
    private const int MaxNodes = 16;
    private const int MaxEdges = 24;
    private const int MaxMembers = 16;
    private static readonly string[] NoLabel = [""];
    private const int MaxLabelCells = 40;
    private const int MaxCanvasCells = 64 * 1024;

    internal enum Status { Success, Unsupported, TooWide, Limit }
    private enum Direction { Down, Up, Right, Left }
    private enum Shape { Rectangle, Decision, Stadium }
    private enum Role { Edge, Node, Text }

    private sealed record Node(string Id)
    {
        public Shape Shape { get; set; }
        public string[]? Label { get; set; }
        public List<string>? Members { get; set; }
        public int Group { get; set; } = -1;
    }

    private sealed record Subgraph(string Id, string Label);

    private readonly record struct Arrow(bool Dashed, char SourceTip, char TargetTip);
    private readonly record struct Edge(int From, int To, string[] Label, Arrow Arrow, string[]? TargetLabel = null);
    private readonly record struct Port(int Source, int Target);
    private readonly record struct Cell(char Symbol, Role Role, string? Text = null);

    private static readonly (string Token, Arrow Arrow)[] EdgeTokens =
    [
        ("<-.->", new(true, '◄', '◄')),
        ("<-->", new(false, '◄', '◄')),
        ("-.->", new(true, '─', '◄')),
        ("-.-", new(true, '─', '─')),
        ("-->", new(false, '─', '◄')),
        ("---", new(false, '─', '─'))
    ];

    internal static Status Render(string source, int width, out IReadOnlyList<Run[]> rows, CliPalette? palette = null)
    {
        rows = [];
        if (source.Length > MaxSourceBytes || Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            return Status.Limit;

        var status = ReadStatements(source.AsSpan(), out var statements);
        if (status != Status.Success) return status;
        var body = statements.GetRange(1, statements.Count - 1);
        var header = statements[0];
        palette ??= CliPalette.Default;
        return header switch
        {
            "sequenceDiagram" => RenderSequence(body, width, palette, out rows),
            "stateDiagram" or "stateDiagram-v2" => RenderStates(body, width, palette, out rows),
            "classDiagram" or "erDiagram" => RenderRelations(body, header == "erDiagram", width, palette, out rows),
            _ => RenderFlowchart(header, body, width, palette, out rows)
        };
    }

    private static Status ReadStatements(ReadOnlySpan<char> input, out List<string> statements)
    {
        statements = [];
        var classBody = false;
        foreach (var lineRange in input.Split('\n'))
        {
            var line = input[lineRange].Trim();
            if (line.StartsWith("%%{", StringComparison.Ordinal)) return Status.Unsupported;
            if (line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (HasEntity(line)) return Status.Unsupported;

            while (!line.IsEmpty)
            {
                var header = statements.Count == 0 ? "" : statements[0];
                if (header == "classDiagram" && (classBody || StartsClassBody(line)))
                {
                    classBody = !line.SequenceEqual("}");
                    statements.Add(line.ToString());
                    break;
                }
                var separator = FindSeparator(line,
                    flowchart: header.StartsWith("flowchart", StringComparison.Ordinal) || header.StartsWith("graph", StringComparison.Ordinal),
                    colonText: header is "classDiagram" or "stateDiagram" or "stateDiagram-v2",
                    literalQuotes: header == "sequenceDiagram");
                var statement = separator < 0 ? line : line[..separator].Trim();
                line = separator < 0 ? default : line[(separator + 1)..].TrimStart();
                if (!statement.IsEmpty) statements.Add(statement.ToString());
            }
        }
        return statements.Count == 0 ? Status.Unsupported : Status.Success;
    }

    // A member line such as +String a;b is one declaration.
    private static bool StartsClassBody(ReadOnlySpan<char> line)
    {
        if (!line.StartsWith("class ", StringComparison.Ordinal)) return false;
        var brace = line.IndexOf('{');
        return brace >= 0 && !line[..brace].Contains(';');
    }

    private static Status RenderFlowchart(string header, IReadOnlyList<string> body, int width,
        CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        if (ReadDirection(header) is not { } direction) return Status.Unsupported;
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var groups = new List<Subgraph>();
        var openGroup = -1;
        foreach (var part in body)
        {
            var statement = part.AsSpan();
            if (statement.StartsWith("subgraph", StringComparison.Ordinal) && statement.Length > 8 && statement[8] is ' ' or '\t')
            {
                if (openGroup >= 0) return Status.Unsupported;
                if (groups.Count == MaxNodes) return Status.Limit;
                var groupStatus = ReadSubgraph(statement[9..].Trim(), groups.Count, out var group);
                if (groupStatus != Status.Success) return groupStatus;
                if (groups.Any(existing => existing.Id == group!.Id)) return Status.Unsupported;
                openGroup = groups.Count;
                groups.Add(group!);
                continue;
            }
            if (statement.SequenceEqual("end"))
            {
                if (openGroup < 0 || !nodes.Any(node => node.Group == openGroup)) return Status.Unsupported;
                openGroup = -1;
                continue;
            }

            var result = ReadStatement(statement, nodes, edges, openGroup);
            if (result != Status.Success) return result;
        }

        if (nodes.Count == 0 || openGroup >= 0 || groups.Any(group => nodes.Any(node => node.Id == group.Id)))
            return Status.Unsupported;
        return Draw(direction, nodes, edges, groups, width, palette, out rows);
    }

    internal static string Notice(Status status) => status switch
    {
        Status.Unsupported => "This Mermaid diagram uses features the terminal renderer doesn't support.",
        Status.TooWide => "This Mermaid diagram doesn't fit the current terminal width.",
        Status.Limit => "This Mermaid diagram exceeds the terminal renderer's size limits.",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static Direction? ReadDirection(ReadOnlySpan<char> statement)
    {
        var end = 0;
        while (end < statement.Length && !char.IsWhiteSpace(statement[end])) end++;
        if (!statement[..end].SequenceEqual("flowchart") && !statement[..end].SequenceEqual("graph")) return null;
        var direction = statement[end..].Trim();
        return direction.IsEmpty ? Direction.Down : ParseDirection(direction);
    }

    private static Direction? ParseDirection(ReadOnlySpan<char> direction) => direction switch
    {
        "TD" or "TB" => Direction.Down,
        "BT" => Direction.Up,
        "LR" => Direction.Right,
        "RL" => Direction.Left,
        _ => null
    };

    // A delimiter in a label (for example A["x; y"]) is ordinary text.
    private static int FindSeparator(ReadOnlySpan<char> line, bool flowchart, bool colonText, bool literalQuotes)
    {
        var quoted = false;
        var close = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var symbol = line[index];
            if (symbol == '"' && !literalQuotes) quoted = !quoted;
            if (quoted) continue;
            if (symbol == ':' && colonText) literalQuotes = true;
            if (close != '\0')
            {
                if (symbol == close) close = '\0';
                continue;
            }
            if (symbol == ';') return index;
            if (flowchart) close = symbol switch { '[' => ']', '{' => '}', '|' => '|', _ => '\0' };
        }
        return -1;
    }

    private static bool HasEntity(ReadOnlySpan<char> line)
    {
        // Decoding is required before these are safe to measure and display.
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] is not ('#' or '&')) continue;
            var end = index + 1;
            while (end < line.Length && IsIdCharacter(line[end])) end++;
            if (end > index + 1 && end < line.Length && line[end] == ';') return true;
        }
        return false;
    }

    private static Status ReadSubgraph(ReadOnlySpan<char> statement, int index, out Subgraph? group)
    {
        group = null;
        if (statement.IsEmpty) return Status.Unsupported;
        Status status;
        string[] label;
        string id;
        var labelStart = statement.IndexOf('[');
        if (labelStart >= 0 && statement[0] != '"')
        {
            var identifier = statement[..labelStart].TrimEnd();
            if (IdentifierLength(identifier) != identifier.Length) return Status.Unsupported;
            id = identifier.ToString();
            var remaining = statement[(labelStart + 1)..];
            status = ReadDelimitedLabel(ref remaining, "]", out label);
            if (!remaining.Trim().IsEmpty) return Status.Unsupported;
        }
        else
        {
            if (statement[0] != '"' && ContainsEdgeSyntax(statement)) return Status.Unsupported;
            status = ReadLabel(statement, out label);
            id = status == Status.Success && label.Length == 1 && !label[0].Any(char.IsWhiteSpace)
                ? label[0]
                : "subGraph" + index.ToString(CultureInfo.InvariantCulture);
        }
        if (status != Status.Success) return status;
        if (label.Length != 1) return Status.Unsupported;
        group = new(id, label[0]);
        return Status.Success;
    }

    private static bool ContainsEdgeSyntax(ReadOnlySpan<char> text) =>
        text.Contains("--", StringComparison.Ordinal) || text.Contains("==", StringComparison.Ordinal)
        || text.Contains("-.", StringComparison.Ordinal) || text.Contains(".-", StringComparison.Ordinal)
        || text.Contains("~~~", StringComparison.Ordinal);

    private static Status ReadStatement(ReadOnlySpan<char> statement, List<Node> nodes, List<Edge> edges, int openGroup)
    {
        var status = ReadNodeGroup(ref statement, nodes, openGroup, out var from);
        if (status != Status.Success) return status;
        while (!statement.TrimStart().IsEmpty)
        {
            status = ReadEdge(ref statement, out var arrow, out var label);
            if (status != Status.Success) return status;
            status = ReadNodeGroup(ref statement, nodes, openGroup, out var to);
            if (status != Status.Success) return status;
            if (from.Count * to.Count > MaxEdges - edges.Count) return Status.Limit;
            foreach (var source in from)
                foreach (var target in to)
                    edges.Add(new(source, target, label, arrow));
            from = to;
        }
        return Status.Success;
    }

    private static Status ReadNodeGroup(ref ReadOnlySpan<char> rest, List<Node> nodes, int openGroup, out List<int> group)
    {
        group = [];
        do
        {
            if (group.Count == MaxEdges) return Status.Limit;
            var status = ReadNode(ref rest, nodes, openGroup, out var index);
            if (status != Status.Success) return status;
            group.Add(index);
            rest = rest.TrimStart();
        } while (Take(ref rest, "&"));
        return Status.Success;
    }

    private static Status ReadNode(ref ReadOnlySpan<char> rest, List<Node> nodes, int openGroup, out int index)
    {
        index = -1;
        if (!TryReadIdentifier(ref rest, out var id) ||
            id is "end" or "subgraph" or "direction" or "style" or "class" or "classDef" or "linkStyle" or "click")
            return Status.Unsupported;
        var nodeStatus = GetOrAddNode(nodes, id, out index);
        if (nodeStatus != Status.Success) return nodeStatus;
        var node = nodes[index];
        if (node.Group < 0) node.Group = openGroup;

        if (rest.StartsWith("[(", StringComparison.Ordinal) || rest.StartsWith("[[", StringComparison.Ordinal)
            || rest.StartsWith("[/", StringComparison.Ordinal) || rest.StartsWith("[\\", StringComparison.Ordinal)
            || rest.StartsWith("{{", StringComparison.Ordinal)) return Status.Unsupported;
        var (shape, close) = Take(ref rest, "([") ? (Shape.Stadium, "])")
            : Take(ref rest, "[") ? (Shape.Rectangle, "]")
            : Take(ref rest, "{") ? (Shape.Decision, "}")
            : (Shape.Rectangle, "");
        if (close.Length == 0) return Status.Success;

        var result = ReadDelimitedLabel(ref rest, close, out var label);
        if (result != Status.Success) return result;
        if (node.Label is { } previous && (shape != node.Shape || !previous.SequenceEqual(label)))
            return Status.Unsupported;
        node.Shape = shape;
        node.Label = label;
        return Status.Success;
    }

    private static Status GetOrAddNode(List<Node> nodes, string id, out int index)
    {
        index = nodes.FindIndex(node => node.Id == id);
        if (index >= 0) return Status.Success;
        if (nodes.Count == MaxNodes) return Status.Limit;
        index = nodes.Count;
        nodes.Add(new(id));
        return Status.Success;
    }

    private static bool TryReadIdentifier(ref ReadOnlySpan<char> rest, out string id)
    {
        rest = rest.TrimStart();
        var length = IdentifierLength(rest);
        if (length <= 0)
        {
            id = "";
            return false;
        }
        id = rest[..length].ToString();
        rest = rest[length..];
        return true;
    }

    private static int IdentifierLength(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !char.IsAsciiLetter(text[0])) return -1;
        var length = 1;
        while (length < text.Length && IsIdCharacter(text[length])) length++;
        return length <= MaxLabelCells ? length : -1;
    }

    private static bool IsIdCharacter(char symbol) => char.IsAsciiLetterOrDigit(symbol) || symbol == '_';

    private static Status ReadEdge(ref ReadOnlySpan<char> rest, out Arrow arrow, out string[] label)
    {
        label = NoLabel;
        arrow = default;
        rest = rest.TrimStart();
        foreach (var (token, candidate) in EdgeTokens)
        {
            if (!Take(ref rest, token)) continue;
            // An adjacent o or x on an undirected edge is a Mermaid endpoint marker.
            if (candidate.TargetTip == '─' && !rest.IsEmpty && rest[0] is 'o' or 'x') return Status.Unsupported;
            arrow = candidate;
            rest = rest.TrimStart();
            return Take(ref rest, "|") ? ReadDelimitedLabel(ref rest, "|", out label) : Status.Success;
        }

        var dashed = Take(ref rest, "-.");
        if (!dashed && !Take(ref rest, "--")) return Status.Unsupported;
        var close = dashed ? ".->" : "-->";
        var end = rest.IndexOf(close, StringComparison.Ordinal);
        if (end <= 0 || !char.IsWhiteSpace(rest[0]) || !char.IsWhiteSpace(rest[end - 1])) return Status.Unsupported;
        var text = rest[..end].Trim();
        rest = rest[(end + close.Length)..];
        arrow = new(dashed, '─', '◄');
        return ReadLabel(text, out label);
    }

    private static Status ReadDelimitedLabel(ref ReadOnlySpan<char> rest, string close, out string[] label)
    {
        label = [];
        int end;
        if (!rest.IsEmpty && rest[0] == '"')
        {
            var closingQuote = rest[1..].IndexOf('"');
            if (closingQuote < 0) return Status.Unsupported;
            end = closingQuote + 2;
            if (!rest[end..].StartsWith(close, StringComparison.Ordinal)) return Status.Unsupported;
        }
        else
        {
            end = rest.IndexOf(close, StringComparison.Ordinal);
            if (end < 0) return Status.Unsupported;
        }

        var text = rest[..end];
        rest = rest[(end + close.Length)..];
        return ReadLabel(text, out label);
    }

    private static Status ReadLabel(ReadOnlySpan<char> text, out string[] label)
    {
        label = [];
        if (text.StartsWith("\"`", StringComparison.Ordinal)) return Status.Unsupported;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        else if (text.ContainsAny("[]{}|")) return Status.Unsupported;
        if (text.Trim().IsEmpty || text.Contains('"')) return Status.Unsupported;
        label = text.ToString().Replace("<br>", "\n", StringComparison.Ordinal)
            .Replace("<br/>", "\n", StringComparison.Ordinal)
            .Replace("<br />", "\n", StringComparison.Ordinal).Split('\n');
        if (label.All(string.IsNullOrWhiteSpace)) return Status.Unsupported;
        for (var index = 0; index < label.Length; index++)
        {
            var line = label[index];
            if (string.IsNullOrWhiteSpace(line)) label[index] = "";
            var status = ValidateLabel(line, allowBlank: true);
            if (status != Status.Success) return status;
        }
        return Status.Success;
    }

    private static Status ValidateLabel(string label, bool allowBlank = false)
    {
        if ((!allowBlank && string.IsNullOrWhiteSpace(label)) || HasMarkup(label) || !TextCanBeDrawn(label))
            return Status.Unsupported;
        return TextWidth.Of(label) > MaxLabelCells ? Status.Limit : Status.Success;
    }

    private static bool HasMarkup(string line)
    {
        for (var index = 0; index < line.Length - 1; index++)
            if (line[index] == '<' && (char.IsAsciiLetter(line[index + 1]) || line[index + 1] is '/' or '!' or '?'))
                return true;
        return false;
    }

    private static bool TextCanBeDrawn(string text)
    {
        var width = 0;
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '┌' or '┐' or '└' or '┘' or '├' or '┤' or '╪' or '◄' or 0xfffd
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
            var cells = TextWidth.OfElement(buffer[..rune.EncodeToUtf16(buffer)]);
            if (cells == 0) return false;
            width += cells;
        }
        return width == TextWidth.Of(text);
    }

    private static bool Take(ref ReadOnlySpan<char> rest, string token)
    {
        if (!rest.StartsWith(token, StringComparison.Ordinal)) return false;
        rest = rest[token.Length..];
        return true;
    }

    private static Status Draw(Direction direction, List<Node> nodes, List<Edge> edges, List<Subgraph> groups,
        int maxWidth, CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        var horizontal = direction is Direction.Right or Direction.Left;
        var labels = nodes.Select(NodeLabel).ToArray();
        var labelWidths = labels.Select(MaxWidth).ToArray();
        var edgeLabels = edges.SelectMany(edge => new[] { edge.Label, edge.TargetLabel ?? NoLabel }).ToArray();
        var edgeLabelWidth = edgeLabels.Select(MaxWidth).DefaultIfEmpty().Max();
        var edgeLabelHeight = edgeLabels.Select(label => label.Length).DefaultIfEmpty(1).Max();
        var boxDepth = horizontal ? labels.Max(label => label.Length) + 2 : labelWidths.Max() + 4;
        var portStride = horizontal ? edgeLabelWidth + 2 : 1;
        var inset = groups.Count == 0 ? 0 : 2;
        var nodeBorder = inset + boxDepth;
        var firstLane = nodeBorder + (horizontal ? edgeLabelHeight + 3 : edgeLabelWidth + 5);

        // Give endpoints and edges independent lanes. A crossing never joins their paths.
        var portCounts = new int[nodes.Count];
        var ports = new Port[edges.Count];
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            var source = portCounts[edge.From];
            portCounts[edge.From] += horizontal ? 1 : edge.Label.Length;
            var target = portCounts[edge.To];
            portCounts[edge.To] += horizontal ? 1 : (edge.TargetLabel ?? NoLabel).Length;
            ports[index] = new(source, target);
        }
        var sizes = Enumerable.Range(0, nodes.Count).Select(index => horizontal
            ? Math.Max(labelWidths[index] + 4, portCounts[index] * portStride + 2)
            : labels[index].Length + portCounts[index] + 2).ToArray();
        var starts = new int[nodes.Count];
        var frames = new (int Start, int Size)[groups.Count];
        var order = GetNodeOrder(nodes, groups.Count, direction is Direction.Left or Direction.Up);
        var length = PlaceNodesAndFrames(order, nodes, groups, horizontal, sizes, starts, frames);

        // Internal edges run inside the group box. Cross-group edges cross the border in their own lanes.
        var isInternal = edges.Select(edge => nodes[edge.From].Group >= 0 && nodes[edge.From].Group == nodes[edge.To].Group).ToArray();
        var frameDepth = nodeBorder + 2;
        if (edges.Count > 0) frameDepth = Math.Max(frameDepth, firstLane + isInternal.Count(value => value) * 2 + 1);
        if (!horizontal && groups.Count > 0) frameDepth = Math.Max(frameDepth, groups.Max(group => TextWidth.Of(group.Label)) + 4);
        var nextInternalLane = firstLane;
        var nextExternalLane = groups.Count == 0 ? firstLane : frameDepth + 2;
        var lanes = new int[edges.Count];
        for (var index = 0; index < edges.Count; index++)
        {
            if (isInternal[index])
            {
                lanes[index] = nextInternalLane;
                nextInternalLane += 2;
            }
            else
            {
                lanes[index] = nextExternalLane;
                nextExternalLane += 2;
            }
        }
        var depth = Math.Max(groups.Count == 0 ? nodeBorder : frameDepth, lanes.DefaultIfEmpty(-1).Max() + 1);

        var width = horizontal ? length : depth;
        var height = horizontal ? depth : length;
        if (width > maxWidth) return Status.TooWide;
        if ((long)width * height > MaxCanvasCells) return Status.Limit;
        var canvas = new Canvas(width, height, horizontal);
        for (var index = 0; index < groups.Count; index++)
        {
            var (start, size) = frames[index];
            DrawBox(canvas, 0, start, frameDepth, size);
            canvas.PlaceLabel([groups[index].Label], horizontal ? start + 2 : 2, horizontal ? 0 : start);
        }
        for (var index = 0; index < nodes.Count; index++)
        {
            var start = starts[index];
            DrawBox(canvas, inset, start, boxDepth, sizes[index], nodes[index].Shape == Shape.Stadium);
            canvas.PlaceLabel(labels[index], horizontal ? start + 2 : inset + 2, horizontal ? inset + 1 : start + 1);
        }
        var endpoints = edges.Select((edge, index) => new Port(
            Offset(edge.From, ports[index].Source), Offset(edge.To, ports[index].Target))).ToArray();
        for (var index = 0; index < edges.Count; index++)
        {
            var (source, target) = endpoints[index];
            for (var along = Math.Min(source, target) + 1; along < Math.Max(source, target); along++)
                canvas.Place(lanes[index], along, edges[index].Arrow.Dashed ? '┆' : '│', Role.Edge);
        }
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            DrawEdge(canvas, edge, endpoints[index], nodeBorder, lanes[index], frameDepth,
                nodes[edge.From].Group >= 0, nodes[edge.To].Group >= 0);
        }
        rows = canvas.Finish(palette);
        return Status.Success;

        int Offset(int node, int port) => starts[node] + (horizontal ? 1 + port * portStride : labels[node].Length + 1 + port);
    }

    private static List<int> GetNodeOrder(List<Node> nodes, int groupCount, bool reverse)
    {
        var order = new List<int>(nodes.Count);
        var emittedGroups = new bool[groupCount];
        for (var index = 0; index < nodes.Count; index++)
        {
            var group = nodes[index].Group;
            if (group < 0)
            {
                order.Add(index);
                continue;
            }
            if (emittedGroups[group]) continue;
            emittedGroups[group] = true;
            for (var member = index; member < nodes.Count; member++)
                if (nodes[member].Group == group) order.Add(member);
        }
        if (reverse) order.Reverse();
        return order;
    }

    private static int PlaceNodesAndFrames(List<int> order, List<Node> nodes, List<Subgraph> groups, bool horizontal,
        int[] sizes, int[] starts, (int Start, int Size)[] frames)
    {
        var along = 0;
        var position = 0;
        while (position < order.Count)
        {
            var node = order[position];
            var group = nodes[node].Group;
            if (group < 0)
            {
                PlaceNextNode();
                continue;
            }
            var frameStart = along;
            along += 2;
            while (position < order.Count && nodes[order[position]].Group == group) PlaceNextNode();
            if (horizontal) along = Math.Max(along, frameStart + TextWidth.Of(groups[group].Label) + 4);
            frames[group] = (frameStart, along - frameStart);
            along += 2;
        }
        return along - 2;

        void PlaceNextNode()
        {
            var node = order[position++];
            starts[node] = along;
            along += sizes[node] + 2;
        }
    }

    private static string[] NodeLabel(Node node)
    {
        var label = node.Label ?? [node.Id];
        if (node.Shape == Shape.Decision)
        {
            label = (string[])label.Clone();
            label[0] = "◇ " + label[0];
        }
        return node.Members is { Count: > 0 } members
            ? [.. label, new string('─', MaxWidth(label)), .. members]
            : label;
    }

    private static int MaxWidth(string[] label) => label.Max(TextWidth.Of);

    private static void DrawBox(Canvas canvas, int inset, int start, int depth, int size, bool rounded = false)
    {
        var end = start + size - 1;
        var far = inset + depth - 1;
        canvas.Place(inset, start, rounded ? '╭' : '┌', Role.Node);
        canvas.Place(far, start, rounded ? '╮' : '┐', Role.Node);
        canvas.Place(inset, end, rounded ? '╰' : '└', Role.Node);
        canvas.Place(far, end, rounded ? '╯' : '┘', Role.Node);
        for (var across = inset + 1; across < far; across++)
        {
            canvas.Place(across, start, '─', Role.Node);
            canvas.Place(across, end, '─', Role.Node);
        }
        for (var along = start + 1; along < end; along++)
        {
            canvas.Place(inset, along, '│', Role.Node);
            canvas.Place(far, along, '│', Role.Node);
        }
    }

    private static void DrawEdge(Canvas canvas, Edge edge, Port endpoints, int depth, int lane,
        int frameDepth, bool sourceInGroup, bool targetInGroup)
    {
        var (source, target) = endpoints;
        DrawPort(source, edge.Arrow.SourceTip, sourceInGroup, source < target ? '┐' : '┘');
        DrawPort(target, edge.Arrow.TargetTip, targetInGroup, source < target ? '┘' : '┐');
        PlaceLabel(source, edge.Label);
        if (edge.TargetLabel is { } targetLabel) PlaceLabel(target, targetLabel);

        void PlaceLabel(int port, string[] label) => canvas.PlaceLabel(label,
            canvas.Horizontal ? port + 1 : depth + 2, canvas.Horizontal ? depth + 1 : port);

        void DrawPort(int port, char tip, bool nodeInGroup, char corner)
        {
            for (var across = depth; across < lane; across++)
            {
                var symbol = nodeInGroup && across == frameDepth - 1 ? '┼'
                    : canvas.Read(across, port) is '│' or '┆' ? '╪'
                    : edge.Arrow.Dashed ? '┄' : '─';
                canvas.Place(across, port, symbol, Role.Edge);
            }
            canvas.Place(depth - 1, port, '├', Role.Node);
            canvas.Place(depth, port, tip, Role.Edge);
            canvas.Place(lane, port, corner, Role.Edge);
        }
    }

    private sealed class Canvas(int width, int height, bool horizontal)
    {
        private readonly Cell[,] _cells = new Cell[height, width];
        public bool Horizontal => horizontal;

        public void Place(int across, int along, char symbol, Role role)
        {
            if (horizontal) _cells[across, along] = new(Transpose(symbol), role);
            else _cells[along, across] = new(symbol, role);
        }

        public char Read(int across, int along) => horizontal
            ? Transpose(_cells[across, along].Symbol)
            : _cells[along, across].Symbol;

        public void PlaceLabel(string[] label, int left, int top)
        {
            for (var index = 0; index < label.Length; index++)
            {
                var row = top + index;
                var column = left;
                var elements = label[index].AsSpan();
                while (!elements.IsEmpty)
                {
                    var length = StringInfo.GetNextTextElementLength(elements);
                    var element = elements[..length];
                    var columns = TextWidth.OfElement(element);
                    _cells[row, column] = new('\0', Role.Text, element.ToString());
                    for (var offset = 1; offset < columns; offset++)
                        _cells[row, column + offset] = new('\0', Role.Text);
                    column += columns;
                    elements = elements[length..];
                }
            }
        }

        public Run[][] Finish(CliPalette theme)
        {
            var rows = new Run[height][];
            var text = new StringBuilder(width);
            for (var row = 0; row < height; row++)
            {
                var runs = new List<Run>();
                var end = width;
                while (end > 0 && IsBlank(_cells[row, end - 1])) end--;
                var role = Role.Edge;
                for (var column = 0; column < end; column++)
                {
                    var cell = _cells[row, column];
                    if (cell.Role != role) Flush();
                    role = cell.Role;
                    if (cell.Text is { } label) text.Append(label);
                    else if (cell.Role != Role.Text) text.Append(cell.Symbol == '\0' ? ' ' : cell.Symbol);
                }
                Flush();
                rows[row] = runs.ToArray();

                void Flush()
                {
                    if (text.Length == 0) return;
                    var style = role switch { Role.Node => theme.CodeVariable, Role.Edge => theme.CodeComment, _ => "" };
                    runs.Add(new(text.ToString(), style));
                    text.Clear();
                }
            }
            return rows;
        }

        private static bool IsBlank(Cell cell) => (cell.Symbol is '\0' or ' ') && string.IsNullOrWhiteSpace(cell.Text);

        private static char Transpose(char symbol) => symbol switch
        {
            '─' => '│', '│' => '─', '┄' => '┆', '┆' => '┄', '┐' => '└', '└' => '┐',
            '╮' => '╰', '╰' => '╮', '├' => '┬', '◄' => '▲', '◁' => '△', _ => symbol
        };
    }
}
