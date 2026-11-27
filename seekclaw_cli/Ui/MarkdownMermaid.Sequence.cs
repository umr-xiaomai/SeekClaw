using static SeekClaw.Cli.Ui.MarkdownLayout;

namespace SeekClaw.Cli.Ui;

internal static partial class MarkdownMermaid
{
    private const int MaxSequenceParticipants = 8;
    private const int MaxSequenceEvents = 64;
    private const int MaxSequenceDepth = 4;

    private enum SequenceKind { Message, Note, OpenFrame, Branch, CloseFrame }

    private sealed record SequenceParticipant(string Id)
    {
        public string? Alias { get; set; }
        public string Label => Alias ?? Id;
    }

    private readonly record struct SequenceEvent(SequenceKind Kind, string Label = "", int From = 0, int To = 0,
        char Stroke = '─', char Tip = '▶')
    {
        public int Height => Kind == SequenceKind.Message && From == To ? 4 : 3;
    }

    private static readonly (string Token, char Stroke, char Tip)[] SequenceArrowTokens =
    [
        ("-->>", '┄', '▶'),
        ("->>", '─', '▶'),
        ("-->", '┄', '┄'),
        ("->", '─', '─'),
        ("--x", '┄', 'x'),
        ("-x", '─', 'x'),
    ];

    private static Status RenderSequence(IReadOnlyList<string> statements, int width, CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        var participants = new List<SequenceParticipant>();
        var events = new List<SequenceEvent>();
        var frames = new Stack<(bool Alternate, bool Branched)>();
        foreach (var statement in statements)
        {
            var rest = statement.AsSpan();
            var actor = Take(ref rest, "actor ");
            if (actor || Take(ref rest, "participant "))
            {
                var declaration = DeclareSequenceParticipant(ref rest, participants, actor);
                if (declaration != Status.Success) return declaration;
                continue;
            }

            var result = ReadSequenceEvent(rest, participants, frames, out var evt);
            if (result != Status.Success) return result;
            if (events.Count == MaxSequenceEvents) return Status.Limit;
            events.Add(evt);
        }

        if (frames.Count > 0 || participants.Count == 0) return Status.Unsupported;
        return DrawSequence(participants, events, width, palette, out rows);
    }

    private static Status DeclareSequenceParticipant(ref ReadOnlySpan<char> rest, List<SequenceParticipant> participants, bool actor)
    {
        var status = ReadSequenceParticipant(ref rest, participants, out var index);
        if (status != Status.Success) return status;
        var participant = participants[index];
        if (participant.Alias is not null) return Status.Unsupported;

        rest = rest.TrimStart();
        var label = participant.Id;
        if (Take(ref rest, "as "))
        {
            label = rest.ToString();
            status = CheckSequenceLabel(label);
            if (status != Status.Success) return status;
        }
        else if (!rest.IsEmpty)
        {
            return Status.Unsupported;
        }
        participant.Alias = actor ? label + " (actor)" : label;
        return Status.Success;
    }

    private static Status ReadSequenceParticipant(ref ReadOnlySpan<char> rest, List<SequenceParticipant> participants, out int index)
    {
        index = -1;
        rest = rest.TrimStart();
        var length = IdentifierLength(rest);
        if (length <= 0) return Status.Unsupported;
        var id = rest[..length].ToString();
        rest = rest[length..];
        index = participants.FindIndex(person => person.Id == id);
        if (index >= 0) return Status.Success;
        if (participants.Count == MaxSequenceParticipants) return Status.Limit;
        index = participants.Count;
        participants.Add(new(id));
        return Status.Success;
    }

    private static Status ReadSequenceEvent(ReadOnlySpan<char> rest, List<SequenceParticipant> participants,
        Stack<(bool Alternate, bool Branched)> frames, out SequenceEvent evt)
    {
        evt = default;
        if (rest.SequenceEqual("end"))
        {
            if (!frames.TryPop(out _)) return Status.Unsupported;
            evt = new(SequenceKind.CloseFrame);
            return Status.Success;
        }

        var space = rest.IndexOf(' ');
        if (space > 0)
        {
            var kind = rest[..space];
            var label = rest[(space + 1)..].ToString();
            if (kind is "else")
            {
                if (!frames.TryPeek(out var frame) || !frame.Alternate || frame.Branched) return Status.Unsupported;
                var status = CheckSequenceLabel(label);
                if (status != Status.Success) return status;
                frames.Pop();
                frames.Push((true, true));
                evt = new(SequenceKind.Branch, rest.ToString());
                return Status.Success;
            }
            if (kind is "loop" or "alt" or "opt" or "critical" or "break")
            {
                var status = CheckSequenceLabel(label);
                if (status != Status.Success) return status;
                if (frames.Count == MaxSequenceDepth) return Status.Limit;
                frames.Push((kind is "alt", false));
                evt = new(SequenceKind.OpenFrame, rest.ToString());
                return Status.Success;
            }
        }

        return Take(ref rest, "Note over ") || Take(ref rest, "note over ")
            ? ReadSequenceNote(rest, participants, out evt)
            : ReadSequenceMessage(rest, participants, out evt);
    }

    private static Status ReadSequenceNote(ReadOnlySpan<char> rest, List<SequenceParticipant> participants, out SequenceEvent evt)
    {
        evt = default;
        var status = ReadSequenceParticipant(ref rest, participants, out var first);
        if (status != Status.Success) return status;
        var second = first;
        rest = rest.TrimStart();
        if (Take(ref rest, ","))
        {
            status = ReadSequenceParticipant(ref rest, participants, out second);
            if (status != Status.Success) return status;
        }
        status = ReadSequenceLabel(rest, out var label);
        if (status == Status.Success)
            evt = new(SequenceKind.Note, label, Math.Min(first, second), Math.Max(first, second));
        return status;
    }

    private static Status ReadSequenceMessage(ReadOnlySpan<char> rest, List<SequenceParticipant> participants, out SequenceEvent evt)
    {
        evt = default;
        var status = ReadSequenceParticipant(ref rest, participants, out var from);
        if (status != Status.Success) return status;
        rest = rest.TrimStart();
        foreach (var (token, stroke, tip) in SequenceArrowTokens)
        {
            if (!Take(ref rest, token)) continue;
            status = ReadSequenceParticipant(ref rest, participants, out var to);
            if (status != Status.Success) return status;
            status = ReadSequenceLabel(rest, out var label);
            if (status == Status.Success) evt = new(SequenceKind.Message, label, from, to, stroke, tip);
            return status;
        }
        return Status.Unsupported;
    }

    private static Status ReadSequenceLabel(ReadOnlySpan<char> rest, out string label)
    {
        label = "";
        rest = rest.TrimStart();
        if (!Take(ref rest, ":")) return Status.Unsupported;
        label = rest.Trim().ToString();
        return CheckSequenceLabel(label);
    }

    private static Status CheckSequenceLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || HasMarkup(label) || !TextCanBeDrawn(label)) return Status.Unsupported;
        return TextWidth.Of(label) <= MaxLabelCells ? Status.Success : Status.Limit;
    }

    private static Status DrawSequence(List<SequenceParticipant> participants, List<SequenceEvent> events,
        int maxWidth, CliPalette palette, out IReadOnlyList<Run[]> rows)
    {
        rows = [];
        var boxWidth = participants.Max(person => TextWidth.Of(person.Label)) + 4;
        var labelWidth = events.Select(evt => TextWidth.Of(evt.Label) + (evt.Kind == SequenceKind.Note ? 6 : 0))
            .DefaultIfEmpty().Max();
        var stride = Math.Max(boxWidth + 2, labelWidth + 4);
        // Leave space for the nested frames on both sides, outside the lifelines.
        const int frameMargin = MaxSequenceDepth * 2 + 2;
        var centers = Enumerable.Range(0, participants.Count)
            .Select(index => frameMargin + boxWidth / 2 + stride * index).ToArray();
        var last = centers[^1];
        var width = Math.Max(last + boxWidth - boxWidth / 2 + frameMargin, last + labelWidth + frameMargin + 4);
        if ((long)width * (3 + 4 * events.Count) > MaxCanvasCells) return Status.Limit;

        var canvas = new Canvas(width, 3 + events.Sum(evt => evt.Height), horizontal: false);
        for (var index = 0; index < participants.Count; index++)
        {
            var left = centers[index] - boxWidth / 2;
            DrawSequenceBox(canvas, left, left + boxWidth - 1, 0, participants[index].Label);
        }

        var top = 3;
        var depth = 0;
        foreach (var evt in events)
        {
            for (var row = top; row < top + evt.Height; row++)
            {
                foreach (var center in centers) canvas.Place(center, row, '│', Role.Edge);
                for (var level = 0; level < depth; level++)
                {
                    canvas.Place(level * 2, row, '│', Role.Node);
                    canvas.Place(width - 1 - level * 2, row, '│', Role.Node);
                }
            }

            switch (evt.Kind)
            {
                case SequenceKind.Message:
                    DrawSequenceMessage(canvas, evt, centers, top);
                    break;
                case SequenceKind.Note:
                    var label = "Note: " + evt.Label;
                    var left = centers[evt.From];
                    var right = Math.Max(centers[evt.To], left + TextWidth.Of(label) + 3);
                    DrawSequenceBox(canvas, left, right, top, label);
                    break;
                case SequenceKind.OpenFrame:
                    depth++;
                    PaintSequenceFrame(canvas, width, top, depth, '┌', '┐', '│', evt.Label);
                    break;
                case SequenceKind.Branch:
                    PaintSequenceFrame(canvas, width, top, depth, '├', '┤', '│', evt.Label);
                    break;
                case SequenceKind.CloseFrame:
                    PaintSequenceFrame(canvas, width, top, depth, '└', '┘', ' ', "");
                    depth--;
                    break;
            }
            top += evt.Height;
        }

        var rendered = canvas.Finish(palette);
        if (rendered.Any(row => row.Sum(run => TextWidth.Of(run.Text)) > maxWidth)) return Status.TooWide;
        rows = rendered;
        return Status.Success;
    }

    private static void DrawSequenceBox(Canvas canvas, int left, int right, int top, string label)
    {
        for (var column = left + 1; column < right; column++)
        {
            canvas.Place(column, top, '─', Role.Node);
            canvas.Place(column, top + 1, ' ', Role.Node);
            canvas.Place(column, top + 2, '─', Role.Node);
        }
        canvas.Place(left, top, '┌', Role.Node);
        canvas.Place(right, top, '┐', Role.Node);
        canvas.Place(left, top + 1, '│', Role.Node);
        canvas.Place(right, top + 1, '│', Role.Node);
        canvas.Place(left, top + 2, '└', Role.Node);
        canvas.Place(right, top + 2, '┘', Role.Node);
        canvas.PlaceLabel([label], left + 2, top + 1);
    }

    private static void DrawSequenceMessage(Canvas canvas, SequenceEvent evt, int[] centers, int top)
    {
        var start = centers[evt.From];
        var end = centers[evt.To];
        var left = Math.Min(start, end);
        var right = Math.Max(start, end);
        var tip = start >= end && evt.Tip == '▶' ? '◀' : evt.Tip;
        canvas.PlaceLabel([evt.Label], left + 2, top);
        if (start == end)
        {
            // Leave a return lane beneath the outgoing stroke of a self-message.
            for (var column = start + 1; column < start + 4; column++)
            {
                canvas.Place(column, top + 1, evt.Stroke, Role.Edge);
                canvas.Place(column, top + 2, evt.Stroke, Role.Edge);
            }
            canvas.Place(start, top + 1, '├', Role.Edge);
            canvas.Place(start + 4, top + 1, '┐', Role.Edge);
            canvas.Place(start + 4, top + 2, '┘', Role.Edge);
            canvas.Place(start, top + 2, tip, Role.Edge);
            return;
        }

        for (var column = left + 1; column < right; column++) canvas.Place(column, top + 1, evt.Stroke, Role.Edge);
        foreach (var center in centers)
            if (center > left && center < right) canvas.Place(center, top + 1, '┼', Role.Edge);
        canvas.Place(start, top + 1, start < end ? '├' : '┤', Role.Edge);
        canvas.Place(end, top + 1, tip, Role.Edge);
    }

    private static void PaintSequenceFrame(Canvas canvas, int width, int top, int depth,
        char leftCorner, char rightCorner, char side, string label)
    {
        var left = (depth - 1) * 2;
        var right = width - 1 - left;
        for (var column = left + 1; column < right; column++) canvas.Place(column, top, '─', Role.Node);
        canvas.Place(left, top, leftCorner, Role.Node);
        canvas.Place(right, top, rightCorner, Role.Node);
        if (label.Length > 0) canvas.PlaceLabel([label], left + 2, top);
        for (var row = top + 1; row < top + 3; row++)
        {
            canvas.Place(left, row, side, Role.Node);
            canvas.Place(right, row, side, Role.Node);
        }
    }
}
