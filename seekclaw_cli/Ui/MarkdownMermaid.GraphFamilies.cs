using static SeekClaw.Cli.Ui.MarkdownLayout;

namespace SeekClaw.Cli.Ui;

internal static partial class MarkdownMermaid
{
    private const string InitialState = "[initial]";
    private const string FinalState = "[final]";

    private static Status RenderStates(IReadOnlyList<string> body, int width, CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        Direction? direction = null;
        foreach (var statement in body)
        {
            var rest = statement.AsSpan();
            if (statement is "state" or "direction" or "note" or "end" or "hide") return Status.Unsupported;
            if (Take(ref rest, "direction "))
            {
                if (!TryReadDirection(ref direction, rest)) return Status.Unsupported;
                continue;
            }
            if (Take(ref rest, "state \""))
            {
                var aliasStatus = ReadStateAlias(rest, nodes);
                if (aliasStatus != Status.Success) return aliasStatus;
                continue;
            }

            var status = ReadStateNode(ref rest, nodes, InitialState, "● initial", out var from);
            if (status != Status.Success) return status;
            rest = rest.Trim();
            var isInitial = nodes[from].Id == InitialState;
            if (!isInitial && rest.IsEmpty) continue;
            if (TryReadColonText(rest, out var description))
            {
                if (isInitial) return Status.Unsupported;
                status = ValidateLabel(description);
                if (status == Status.Success) status = AddStateDescription(nodes[from], description);
                if (status != Status.Success) return status;
                continue;
            }

            if (!Take(ref rest, "-->")) return Status.Unsupported;
            status = ReadStateNode(ref rest, nodes, FinalState, "◎ final", out var to);
            if (status != Status.Success) return status;
            status = ReadEdgeCaption(rest, required: false, allowQuotes: true, out var label);
            if (status != Status.Success) return status;
            if (edges.Count == MaxEdges) return Status.Limit;
            edges.Add(new(from, to, label.Length == 0 ? NoLabel : [label], new(false, '─', '◄')));
        }
        return nodes.Count == 0 ? Status.Unsupported : Draw(direction ?? Direction.Down, nodes, edges, [], width, palette, out rows);
    }

    private static Status ReadStateAlias(ReadOnlySpan<char> alias, List<Node> nodes)
    {
        var quote = alias.IndexOf('"');
        if (quote < 0) return Status.Unsupported;
        var label = alias[..quote].ToString();
        var rest = alias[(quote + 1)..].TrimStart();
        if (!Take(ref rest, "as ") || !TryReadIdentifier(ref rest, out var id) || !rest.Trim().IsEmpty)
            return Status.Unsupported;
        var status = ValidateLabel(label);
        if (status != Status.Success) return status;
        status = GetOrAddNode(nodes, id, out var index);
        return status == Status.Success ? AddStateDescription(nodes[index], label) : status;
    }

    private static Status ReadStateNode(ref ReadOnlySpan<char> rest, List<Node> nodes, string pseudoId,
        string pseudoLabel, out int index)
    {
        index = -1;
        rest = rest.TrimStart();
        if (Take(ref rest, "[*]"))
        {
            var status = GetOrAddNode(nodes, pseudoId, out index);
            if (status == Status.Success) nodes[index].Label = [pseudoLabel];
            return status;
        }
        if (!TryReadIdentifier(ref rest, out var id)) return Status.Unsupported;
        if (pseudoId == InitialState && IsAccessibilityDirective(id, rest.TrimStart(), ignoreCase: true, allowBlock: false))
            return Status.Unsupported;
        return GetOrAddNode(nodes, id, out index);
    }

    private static Status AddStateDescription(Node node, string text)
    {
        if (node.Label is not null) return AddMember(node, text);
        node.Label = [text];
        return Status.Success;
    }

    private static Status RenderRelations(IReadOnlyList<string> body, bool entityRelationship, int width,
        CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        Direction? direction = null;
        Node? openNode = null;
        foreach (var statement in body)
        {
            if (openNode is not null)
            {
                if (statement == "}")
                {
                    openNode = null;
                    continue;
                }
                var memberStatus = ReadRelationMember(statement, entityRelationship, out var member);
                if (memberStatus == Status.Success) memberStatus = AddMember(openNode, member);
                if (memberStatus != Status.Success) return memberStatus;
                continue;
            }

            var rest = statement.AsSpan();
            if (Take(ref rest, "direction "))
            {
                if (!TryReadDirection(ref direction, rest)) return Status.Unsupported;
                continue;
            }
            var classDeclaration = !entityRelationship && Take(ref rest, "class ");
            if (!TryReadIdentifier(ref rest, out var id)) return Status.Unsupported;
            rest = rest.TrimStart();
            if (!classDeclaration && IsAccessibilityDirective(id, rest, entityRelationship, allowBlock: true))
                return Status.Unsupported;

            var status = GetOrAddNode(nodes, id, out var from);
            if (status != Status.Success) return status;
            if ((classDeclaration || entityRelationship) && rest.SequenceEqual("{"))
            {
                openNode = nodes[from];
                continue;
            }
            if (classDeclaration || (entityRelationship && rest.IsEmpty))
            {
                if (!rest.IsEmpty) return Status.Unsupported;
                continue;
            }
            if (!entityRelationship && TryReadColonText(rest, out var classMember))
            {
                status = ValidateLabel(classMember);
                if (status == Status.Success) status = AddMember(nodes[from], classMember);
                if (status != Status.Success) return status;
                continue;
            }

            status = ReadRelation(rest, from, nodes, entityRelationship, out var edge);
            if (status != Status.Success) return status;
            if (edges.Count == MaxEdges) return Status.Limit;
            edges.Add(edge);
        }
        return openNode is not null || nodes.Count == 0
            ? Status.Unsupported
            : Draw(direction ?? Direction.Down, nodes, edges, [], width, palette, out rows);
    }

    private static bool TryReadDirection(ref Direction? direction, ReadOnlySpan<char> value)
    {
        if (direction is not null || ParseDirection(value.Trim()) is not { } parsed) return false;
        direction = parsed;
        return true;
    }

    private static bool IsAccessibilityDirective(string id, ReadOnlySpan<char> rest, bool ignoreCase, bool allowBlock)
    {
        if (rest.IsEmpty) return false;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (rest[0] == ':' && id.Equals("accTitle", comparison)) ||
            ((rest[0] == ':' || (allowBlock && rest[0] == '{')) && id.Equals("accDescr", comparison));
    }

    private static bool TryReadColonText(ReadOnlySpan<char> rest, out string text)
    {
        if (rest.Length > 0 && rest[0] == ':' && (rest.Length == 1 || rest[1] != ':'))
        {
            text = rest[1..].Trim().ToString();
            return true;
        }
        text = "";
        return false;
    }

    private static Status ReadEdgeCaption(ReadOnlySpan<char> rest, bool required, bool allowQuotes, out string label)
    {
        rest = rest.Trim();
        if (TryReadColonText(rest, out label))
            return allowQuotes || !label.Contains('"') ? ValidateLabel(label) : Status.Unsupported;
        return required || !rest.IsEmpty ? Status.Unsupported : Status.Success;
    }

    private static Status AddMember(Node node, string member)
    {
        var members = node.Members ??= [];
        if (members.Count == MaxMembers) return Status.Limit;
        members.Add(member);
        return Status.Success;
    }

    private static Status ReadRelation(ReadOnlySpan<char> rest, int from, List<Node> nodes, bool entityRelationship,
        out Edge edge)
    {
        edge = default;
        var sourceCardinality = "";
        if (!entityRelationship)
        {
            var cardStatus = ReadClassCardinality(ref rest, out sourceCardinality);
            if (cardStatus != Status.Success) return cardStatus;
        }
        rest = rest.TrimStart();
        var solid = rest.IndexOf("--", StringComparison.Ordinal);
        var dotted = rest.IndexOf("..", StringComparison.Ordinal);
        var dashed = dotted >= 0 && (solid < 0 || dotted < solid);
        var split = dashed ? dotted : solid;
        if (split is < 0 or > 2) return Status.Unsupported;
        var left = rest[..split];
        rest = rest[(split + 2)..];
        char sourceTip, targetTip;
        string targetCardinality;
        if (entityRelationship)
        {
            if (rest.Length < 2 || ErCardinality(left, target: false) is not { } first ||
                ErCardinality(rest[..2], target: true) is not { } second) return Status.Unsupported;
            sourceCardinality = first;
            targetCardinality = second;
            sourceTip = targetTip = '─';
            rest = rest[2..];
        }
        else
        {
            sourceTip = left switch { "" => '─', "<" => '◄', "<|" => '◁', "*" => '◆', "o" => '◇', _ => '\0' };
            if (sourceTip == '\0') return Status.Unsupported;
            targetTip = ReadClassTargetTip(ref rest);
            var cardStatus = ReadClassCardinality(ref rest, out targetCardinality);
            if (cardStatus != Status.Success) return cardStatus;
        }

        if (!TryReadIdentifier(ref rest, out var id)) return Status.Unsupported;
        var status = GetOrAddNode(nodes, id, out var to);
        if (status != Status.Success) return status;
        status = ReadEdgeCaption(rest, required: entityRelationship, allowQuotes: !entityRelationship, out var label);
        if (status != Status.Success) return status;
        if (sourceCardinality.Length > 0)
            label = label.Length == 0 ? $"({sourceCardinality})" : $"({sourceCardinality}) {label}";
        edge = new(from, to, label.Length == 0 ? NoLabel : [label], new(dashed, sourceTip, targetTip),
            targetCardinality.Length == 0 ? null : [$"({targetCardinality})"]);
        return Status.Success;
    }

    private static string? ErCardinality(ReadOnlySpan<char> token, bool target) => token switch
    {
        "||" => "1",
        "|o" when !target => "0..1",
        "o|" when target => "0..1",
        "}|" when !target => "1..many",
        "|{" when target => "1..many",
        "}o" when !target => "0..many",
        "o{" when target => "0..many",
        _ => null
    };

    private static char ReadClassTargetTip(ref ReadOnlySpan<char> rest)
    {
        if (Take(ref rest, "|>")) return '◁';
        if (Take(ref rest, ">")) return '◄';
        if (Take(ref rest, "*")) return '◆';
        // A bare identifier such as order takes precedence over the aggregation marker.
        if (!rest.IsEmpty && rest[0] == 'o' && (rest.Length == 1 || !IsIdCharacter(rest[1])))
        {
            rest = rest[1..];
            return '◇';
        }
        return '─';
    }

    private static Status ReadClassCardinality(ref ReadOnlySpan<char> rest, out string value)
    {
        value = "";
        rest = rest.TrimStart();
        if (!Take(ref rest, "\"")) return Status.Success;
        var quote = rest.IndexOf('"');
        if (quote < 0) return Status.Unsupported;
        value = rest[..quote].ToString();
        rest = rest[(quote + 1)..];
        return ValidateLabel(value);
    }

    private static Status ReadRelationMember(string statement, bool entityRelationship, out string member)
    {
        member = statement;
        if (!entityRelationship)
            return statement.AsSpan().ContainsAny('{', '}') ? Status.Unsupported : ValidateLabel(statement);

        var rest = statement.AsSpan();
        if (!TryReadIdentifier(ref rest, out var type) || rest.IsEmpty || !char.IsWhiteSpace(rest[0]) ||
            !TryReadIdentifier(ref rest, out var name)) return Status.Unsupported;
        rest = rest.Trim();
        var comment = "";
        var quote = rest.IndexOf('"');
        if (quote >= 0)
        {
            if (rest.Length <= quote + 1 || rest[^1] != '"' || rest[(quote + 1)..^1].Contains('"'))
                return Status.Unsupported;
            comment = rest[(quote + 1)..^1].ToString();
            var commentStatus = ValidateLabel(comment);
            if (commentStatus != Status.Success) return commentStatus;
            rest = rest[..quote].TrimEnd();
        }
        foreach (var range in rest.Split(','))
        {
            if (rest.IsEmpty) break;
            if (rest[range].Trim() is not ("PK" or "FK" or "UK")) return Status.Unsupported;
        }
        member = $"{type} {name}";
        if (!rest.IsEmpty) member += " " + rest.ToString();
        if (comment.Length > 0) member += " — " + comment;
        return ValidateLabel(member);
    }
}
