using System.Collections.Frozen;
using System.Globalization;
using static SeekClaw.Cli.Ui.MarkdownLayout;

namespace SeekClaw.Cli.Ui;

/// <summary>A lightweight, stateful lexer for the code in a single Markdown fence.</summary>
internal sealed class MarkdownCode(string language, CliPalette theme)
{
    [Flags]
    private enum Syntax
    {
        None = 0,
        BlockComments = 1,
        TripleQuotes = 2,
        Backticks = 4,
        RawBackticks = 8,
        MultilineQuotes = 16,
        ShellVariables = 32,
        VerbatimStrings = 64,
        DoubledQuotes = 128,
    }

    private sealed record Language(FrozenSet<string> Keywords, string LineComment, Syntax Features = Syntax.None,
        char Escape = '\\');
    private readonly record struct StringDelimiter(string End, bool Multiline, char Escape = '\\', bool Doubled = false);

    private const int MaxLineLength = 4 * 1024;
    private const int MaxBlockLength = 256 * 1024;
    private const int MaxLineCount = 10_000;

    private static readonly Language CLike = new(Words("abstract as async await bool break case catch char class const constexpr continue default delegate double else enum export extern false final finally float fn for from func if impl import in int interface internal let long match mod mut namespace new null override package private protected pub public readonly ref return sealed short static string struct super switch this throw true try type typeof unsafe use using var virtual void while yield"), "//", Syntax.BlockComments);
    private static readonly Language CSharp = CLike with { Features = CLike.Features | Syntax.TripleQuotes | Syntax.VerbatimStrings };
    private static readonly Language Java = CLike with { Features = CLike.Features | Syntax.TripleQuotes };
    private static readonly Language JavaScript = CLike with { Features = CLike.Features | Syntax.Backticks };
    private static readonly Language Go = CLike with { Features = CLike.Features | Syntax.RawBackticks };
    private static readonly Language Python = new(Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield"), "#", Syntax.TripleQuotes);
    private static readonly Language Shell = new(Words("begin break case catch continue do done elif else end esac fi for foreach function if in local param return set then throw trap try until while"), "#", Syntax.MultilineQuotes | Syntax.Backticks | Syntax.ShellVariables);
    private static readonly Language PowerShell = new(Shell.Keywords, "#", Syntax.MultilineQuotes | Syntax.ShellVariables | Syntax.DoubledQuotes, Escape: '`');
    private static readonly Language Data = new(Words("true false null"), "");
    private static readonly Language Yaml = new(Data.Keywords, "#");
    private static readonly Language Toml = Yaml with { Features = Syntax.TripleQuotes };
    private static readonly Language Sql = new(Words("SELECT FROM AS WHERE INTO JOIN INNER OUTER LEFT RIGHT GROUP ORDER BY HAVING LIMIT OFFSET ASC DESC DISTINCT INSERT UPDATE DELETE SET VALUES TABLE CREATE ALTER DROP UNION ALL NULL NOT AND OR IN CASE WHEN THEN ELSE END BEGIN COMMIT ROLLBACK WITH", StringComparer.OrdinalIgnoreCase), "--", Syntax.BlockComments | Syntax.MultilineQuotes | Syntax.DoubledQuotes, Escape: '\0');

    private readonly Language? _language = FindLanguage(language);
    private bool _blockComment;
    private StringDelimiter? _string;
    private int _remainingCharacters = MaxBlockLength;
    private int _remainingLines = MaxLineCount;
    private bool _budgetExceeded;

    private static FrozenSet<string> Words(string words, StringComparer? comparer = null) =>
        words.Split(' ').ToFrozenSet(comparer ?? StringComparer.Ordinal);

    private static Language? FindLanguage(string language) => language.ToLowerInvariant() switch
    {
        "c" or "h" or "cpp" or "c++" or "rust" or "rs" or "swift" or "kt" or "kotlin" => CLike,
        "cs" or "csharp" or "c#" => CSharp,
        "java" => Java,
        "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" => JavaScript,
        "go" or "golang" => Go,
        "py" or "python" or "python3" => Python,
        "sh" or "bash" or "zsh" or "fish" => Shell,
        "powershell" or "pwsh" or "ps1" => PowerShell,
        "json" => Data,
        "yaml" or "yml" => Yaml,
        "toml" => Toml,
        "sql" => Sql,
        _ => null,
    };

    public IReadOnlyList<Run> HighlightLine(string text)
    {
        if (_language is not { } syntax || _budgetExceeded) return [new(text)];
        if (text.Length > MaxLineLength || text.Length > _remainingCharacters || _remainingLines == 0)
        {
            // A skipped line may have closed a multiline token. Leave the remaining code unstyled.
            _budgetExceeded = true;
            return [new(text)];
        }
        _remainingCharacters -= text.Length;
        _remainingLines--;

        var keywords = syntax.Keywords.GetAlternateLookup<ReadOnlySpan<char>>();
        var runs = new List<Run>();
        var plainStart = 0;
        var expectFunctionName = false;
        for (var position = 0; position < text.Length;)
        {
            var start = position;
            var current = text[position];
            var canBeFunctionName = expectFunctionName;
            if (!char.IsWhiteSpace(current)) expectFunctionName = false;
            string? style = null;
            if (_string is { } continuation)
            {
                position = ReadString(text, position, continuation);
                style = theme.CodeString;
            }
            else if (_blockComment || syntax.Features.HasFlag(Syntax.BlockComments) && At(text, position, "/*"))
            {
                var close = text.IndexOf("*/", _blockComment ? position : position + 2, StringComparison.Ordinal);
                _blockComment = close < 0;
                position = _blockComment ? text.Length : close + 2;
                style = theme.CodeComment;
            }
            else if (syntax.LineComment.Length > 0 && At(text, position, syntax.LineComment))
            {
                position = text.Length;
                style = theme.CodeComment;
            }
            else if (TryOpenString(text, position, syntax, out var delimiter, out var openingLength))
            {
                position = ReadString(text, position + openingLength, delimiter);
                style = theme.CodeString;
            }
            else if (syntax.Features.HasFlag(Syntax.ShellVariables) && current == '$' &&
                position + 1 < text.Length && IsWordPart(text[position + 1]))
            {
                position = EndOfWord(text, position + 1);
                style = theme.CodeVariable;
            }
            else if (char.IsAsciiDigit(current))
            {
                position = EndOfNumber(text, position);
                style = theme.CodeNumber;
            }
            else if (IsWordPart(current))
            {
                position = EndOfWord(text, position);
                var word = text.AsSpan(start, position - start);
                if (keywords.Contains(word))
                {
                    style = theme.CodeKeyword;
                    expectFunctionName = word is "fn" or "def" or "func" or "function";
                }
                else if (canBeFunctionName) style = theme.CodeFunction;
            }
            else
            {
                position++;
                if (current is '(' or ')' or '{' or '}' or '[' or ']' or ',' or ';') style = theme.CodePunctuation;
            }

            if (style is null) continue;
            if (plainStart < start) runs.Add(new(text[plainStart..start], theme.CodeText));
            runs.Add(new(text[start..position], style));
            plainStart = position;
        }

        if (plainStart < text.Length || runs.Count == 0) runs.Add(new(text[plainStart..], theme.CodeText));
        return runs;
    }

    private static bool At(string text, int position, string token) =>
        text.AsSpan(position).StartsWith(token, StringComparison.Ordinal);

    private static bool TryOpenString(string text, int position, Language syntax, out StringDelimiter delimiter,
        out int length)
    {
        var features = syntax.Features;
        var quote = text[position];
        if (quote is '\'' or '"')
        {
            var end = quote == '\'' ? "'" : "\"";
            var triple = quote == '\'' ? "'''" : "\"\"\"";
            if (features.HasFlag(Syntax.TripleQuotes) && At(text, position, triple))
                delimiter = new(triple, Multiline: true);
            else
            {
                var escape = quote == '\'' && features.HasFlag(Syntax.ShellVariables) ? '\0' : syntax.Escape;
                delimiter = new(end, features.HasFlag(Syntax.MultilineQuotes), escape, features.HasFlag(Syntax.DoubledQuotes));
            }
            length = delimiter.End.Length;
            return true;
        }
        if (quote == '`' && (features & (Syntax.Backticks | Syntax.RawBackticks)) != 0)
        {
            delimiter = new("`", Multiline: true, Escape: features.HasFlag(Syntax.Backticks) ? '\\' : '\0');
            length = 1;
            return true;
        }
        if (quote == '@' && features.HasFlag(Syntax.VerbatimStrings) && At(text, position, "@\""))
        {
            delimiter = new("\"", Multiline: true, Escape: '\0', Doubled: true);
            length = 2;
            return true;
        }
        delimiter = default;
        length = 0;
        return false;
    }

    private int ReadString(string text, int position, StringDelimiter delimiter)
    {
        var continued = false;
        while (position < text.Length)
        {
            if (delimiter.Escape != '\0' && text[position] == delimiter.Escape)
            {
                continued = position + 1 == text.Length;
                position = Math.Min(position + 2, text.Length);
            }
            else if (At(text, position, delimiter.End))
            {
                position += delimiter.End.Length;
                if (!delimiter.Doubled || !At(text, position, delimiter.End))
                {
                    _string = null;
                    return position;
                }
                position += delimiter.End.Length;
            }
            else position++;
        }
        _string = delimiter.Multiline || continued ? delimiter : null;
        return position;
    }

    private static bool IsWordPart(char c) => c == '_' || char.IsLetterOrDigit(c) ||
        char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static int EndOfWord(string text, int position)
    {
        while (position < text.Length && IsWordPart(text[position])) position++;
        return position;
    }

    private static int EndOfNumber(string text, int position)
    {
        var end = EndOfWord(text, position);
        if (end + 1 < text.Length && text[end] == '.' && char.IsAsciiDigit(text[end + 1]))
            end = EndOfWord(text, end + 1);
        if (end + 1 < text.Length && text[end - 1] is ('e' or 'E') && text[end] is ('+' or '-') &&
            char.IsAsciiDigit(text[end + 1]))
            end = EndOfWord(text, end + 1);
        return end;
    }
}
