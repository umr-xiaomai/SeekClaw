using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace SeekClaw.Cli.Ui;

/// <summary>Caches completed Markdown blocks while an assistant message is streaming.</summary>
public sealed class MarkdownStreamRenderer
{
    private int _width;
    private string? _workingDirectory;
    private CliPalette? _palette;
    private int _stableBlocks;
    private SourceSpan _lastStableBlock;
    private readonly List<string> _stableLines = [];
    private string? _completedSource;
    private MarkdownDocument? _completedDocument;
    private string? _visibleSource;
    private IReadOnlyList<string> _visibleLines = [];

    // A table can change the layout of every earlier row when it gains one cell.
    // Keep geometry-dependent blocks in the viewport until another block follows.
    internal int SpillableRows { get; private set; }
    internal int StableRows { get; private set; }

    public IReadOnlyList<string> Render(string source, int width, int maxLines)
    {
        if (maxLines <= 0) return [];
        return RenderAll(source, width).TakeLast(maxLines).ToArray();
    }

    internal IReadOnlyList<string> RenderAll(string source, int width, string? workingDirectory = null, CliPalette? palette = null)
    {
        width = Math.Max(1, width);
        palette ??= CliPalette.Default;
        if (_width != width || _workingDirectory != workingDirectory || _palette != palette || source.Length == 0) Reset();
        _width = width;
        _workingDirectory = workingDirectory;
        _palette = palette;
        if (source.Length == 0) return [];

        // Table and fence structure is evaluated on complete source lines. A
        // partial prose line can still preview while the provider is writing it.
        var normalized = Ansi.StripControls(source);
        var boundary = normalized.LastIndexOf('\n') + 1;
        var completedSource = normalized[..boundary];
        if (_completedDocument is null || completedSource != _completedSource)
        {
            _completedSource = completedSource;
            _completedDocument = MarkdownAnsi.ParseNormalized(completedSource);
        }
        var completed = _completedDocument;
        var partial = normalized.AsSpan(boundary);
        var visible = normalized;
        if (HoldPartialLine(completed.LastOrDefault(), completedSource, partial))
            visible = _visibleSource is { } previous && previous.Length >= boundary &&
                normalized.StartsWith(previous, StringComparison.Ordinal) ? previous : completedSource;
        else
        {
            var linkStart = FindUnfinishedLinkDestination(partial);
            if (linkStart >= 0) visible = normalized[..(boundary + linkStart)];
        }
        if (visible == _visibleSource) return _visibleLines;

        // Parse the visible document as a whole so later reference definitions
        // can resolve earlier links and block spans use the same source offsets.
        var document = visible == completedSource ? completed : MarkdownAnsi.ParseNormalized(visible);
        var hasReferenceDefinitions = document.GetLinkReferenceDefinitions(false) is not null;
        var nextStableBlocks = Math.Max(0, document.Count - 1);
        if (hasReferenceDefinitions || _stableBlocks > nextStableBlocks || _stableBlocks > 0 &&
            !_lastStableBlock.Equals(document[_stableBlocks - 1].Span))
            ResetPrefix();

        MarkdownAnsi.AppendDocument(_stableLines, document.Skip(_stableBlocks).Take(nextStableBlocks - _stableBlocks), width, workingDirectory, palette);
        _stableBlocks = nextStableBlocks;
        if (_stableBlocks > 0) _lastStableBlock = document[_stableBlocks - 1].Span;

        var result = new List<string>(_stableLines);
        MarkdownAnsi.AppendDocument(result, document.Skip(_stableBlocks), width, workingDirectory, palette);
        var held = document.LastOrDefault() is { } tail && NeedsLayoutHoldback(tail);
        SpillableRows = held ? _stableLines.Count : result.Count;

        // A new top-level block completes the earlier ones. Also release a final
        // prose block once it ends with a blank line. A table or transformable
        // code block remains editable while it is the last block.
        StableRows = _stableLines.Count;
        if (!held && visible == completedSource && EndsWithBlankLine(completedSource))
            StableRows = result.Count;

        _visibleSource = visible;
        return _visibleLines = result;
    }

    private static bool HoldPartialLine(Block? tail, string completed, ReadOnlySpan<char> partial)
    {
        if (partial.IsEmpty) return false;
        if (partial is "`" or "``" or "~" or "~~" ||
            partial[0] is ' ' or '\t' or '>' || partial.Contains('|') ||
            partial.StartsWith("```", StringComparison.Ordinal) ||
            partial.StartsWith("~~~", StringComparison.Ordinal)) return true;

        return OpenCodeFence(tail) || !EndsWithBlankLine(completed) &&
            (tail is CodeBlock and not FencedCodeBlock and not MathBlock or Table || LastCompleteLine(completed).Contains('|'));
    }

    // Keep the link label visible while its destination arrives. The final Markdown
    // parser still decides whether the completed text constitutes a link.
    private static int FindUnfinishedLinkDestination(ReadOnlySpan<char> partial)
    {
        if (!partial.Contains("](", StringComparison.Ordinal)) return -1;

        var brackets = 0;
        var codeTicks = 0;
        for (var index = 0; index < partial.Length; index++)
        {
            var character = partial[index];
            if (character == '`')
            {
                var first = index;
                while (index + 1 < partial.Length && partial[index + 1] == '`') index++;
                var ticks = index - first + 1;
                if (codeTicks == 0) codeTicks = ticks;
                else if (codeTicks == ticks) codeTicks = 0;
                continue;
            }
            if (codeTicks > 0) continue;
            if (character == '\\')
            {
                index++;
                continue;
            }
            if (character == '[') brackets++;
            if (character != ']' || brackets == 0) continue;
            brackets--;
            if (index + 1 == partial.Length || partial[index + 1] != '(') continue;

            var open = index + 1;
            var close = FindLinkClose(partial[(open + 1)..]);
            if (close < 0) return open;
            index = open + 1 + close;
        }
        return -1;
    }

    private static int FindLinkClose(ReadOnlySpan<char> destination)
    {
        var depth = 1;
        var delimiter = '\0';
        var whitespace = false;
        for (var index = 0; index < destination.Length; index++)
        {
            var character = destination[index];
            if (character == '\\')
            {
                index++;
                whitespace = false;
                continue;
            }
            if (delimiter != '\0')
            {
                if (character == delimiter) delimiter = '\0';
            }
            else if (depth == 1 && character == '<' && (index == 0 || whitespace))
                delimiter = '>';
            else if (depth == 1 && whitespace && character is '\'' or '"')
                delimiter = character;
            else if (character == '(')
                depth++;
            else if (character == ')' && --depth == 0)
                return index;
            whitespace = char.IsWhiteSpace(character);
        }
        return -1;
    }

    private static bool OpenCodeFence(Block? block) => block switch
    {
        FencedCodeBlock fence => fence.ClosingFencedCharCount < fence.OpeningFencedCharCount,
        ContainerBlock container when container.LastOrDefault() is { } last => OpenCodeFence(last),
        _ => false,
    };

    private static ReadOnlySpan<char> LastCompleteLine(string completed)
    {
        var source = completed.AsSpan();
        if (!source.IsEmpty) source = source[..^1];
        return source[(source.LastIndexOf('\n') + 1)..];
    }

    private static bool EndsWithBlankLine(string completed) => completed.Length > 0 && LastCompleteLine(completed).IsWhiteSpace();

    private static bool NeedsLayoutHoldback(Block block)
    {
        if (block is Table or MathBlock) return true;
        if (block is FencedCodeBlock fence && MarkdownAnsi.CodeLanguage(fence).ToLowerInvariant() is "md" or "markdown" or "mermaid")
            return true;
        return block is ContainerBlock container && container.LastOrDefault() is { } last && NeedsLayoutHoldback(last);
    }

    public void Reset()
    {
        _width = 0;
        _workingDirectory = null;
        _palette = null;
        _completedSource = null;
        _completedDocument = null;
        _visibleSource = null;
        _visibleLines = [];
        ResetPrefix();
    }

    private void ResetPrefix()
    {
        _stableBlocks = 0;
        _lastStableBlock = default;
        _stableLines.Clear();
        SpillableRows = 0;
        StableRows = 0;
    }
}
