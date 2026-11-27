using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>Renders a small, bounded subset of TeX as terminal text.</summary>
internal static class MarkdownMath
{
    private const int MaxLength = 1024;
    private const int MaxDepth = 12;
    private const string SuperSource = "0123456789+-=()abcdefghijklmnoprstuvwxyz";
    private const string SuperChars = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻ";
    private const string SubSource = "0123456789+-=()aehijklmnoprstuvxβγρφχ";
    private const string SubChars = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓᵦᵧᵨᵩᵪ";

    private static readonly Rune[] CalligraphicLetters = "𝒜ℬ𝒞𝒟ℰℱ𝒢ℋℐ𝒥𝒦ℒℳ𝒩𝒪𝒫𝒬ℛ𝒮𝒯𝒰𝒱𝒲𝒳𝒴𝒵".EnumerateRunes().ToArray();

    private static readonly FrozenDictionary<string, string> Symbols = new Dictionary<string, string>
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ",
        ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ",
        ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
        ["pi"] = "π", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π",
        ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["times"] = "×", ["cdot"] = "·", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["ast"] = "∗",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠",
        ["approx"] = "≈", ["sim"] = "∼", ["simeq"] = "≃", ["equiv"] = "≡", ["propto"] = "∝",
        ["infty"] = "∞", ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["partial"] = "∂", ["nabla"] = "∇",
        ["in"] = "∈", ["notin"] = "∉", ["subseteq"] = "⊆", ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["leftrightarrow"] = "↔", ["mapsto"] = "↦",
        ["ldots"] = "…", ["cdots"] = "⋯", ["prime"] = "′", ["hbar"] = "ℏ", ["ell"] = "ℓ",
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["log"] = "log", ["ln"] = "ln", ["exp"] = "exp",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static string? Delimiter(string name) => name switch
    {
        "langle" => "⟨", "rangle" => "⟩",
        "lbrace" or "{" => "{", "rbrace" or "}" => "}",
        "lbrack" => "[", "rbrack" => "]",
        "vert" or "lvert" or "rvert" => "|", "Vert" or "lVert" or "rVert" or "|" => "‖",
        "lfloor" => "⌊", "rfloor" => "⌋", "lceil" => "⌈", "rceil" => "⌉",
        _ => null
    };

    public static bool TryRenderInline(string source, out string rendered)
    {
        rendered = "";
        // Stay conservative around currency, shell expansion and ordinary prose.
        if (source.Length is 0 or > MaxLength || source[0] is '(' or '{' ||
            char.IsWhiteSpace(source[0]) || char.IsWhiteSpace(source[^1]) || source.AsSpan().ContainsAny('\r', '\n'))
            return false;
        var hasMathSyntax = source.AsSpan().IndexOfAny("\\^_=+-*/<>".AsSpan()) >= 0;
        if (!hasMathSyntax && source != "0" && (source.Length != 1 || !char.IsLetter(source[0]))) return false;
        if (source.IndexOf('_') > 1 && source.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_')) return false;

        var parts = new Parser(source).Read();
        if (parts is null) return false;
        rendered = Join(parts);
        return !string.IsNullOrWhiteSpace(rendered);
    }

    public static bool TryRenderBlock(string source, int width, out IReadOnlyList<string> lines)
    {
        lines = [];
        if (source.Length is 0 or > MaxLength) return false;
        var parts = new Parser(source.Trim()).Read();
        if (parts is null) return false;

        if (parts.All(static part => part.Fraction is null))
        {
            var text = Join(parts);
            if (string.IsNullOrWhiteSpace(text)) return false;
            lines = MarkdownLayout.Wrap([new(text)], width);
            return true;
        }

        var above = new StringBuilder();
        var middle = new StringBuilder();
        var below = new StringBuilder();
        var previousWasFraction = false;
        foreach (var part in parts)
        {
            if (part.Fraction is { } fraction)
            {
                if (previousWasFraction)
                {
                    above.Append(' ');
                    middle.Append(' ');
                    below.Append(' ');
                }
                var barWidth = Math.Max(TextWidth.Of(fraction.Numerator), TextWidth.Of(fraction.Denominator));
                AppendCentered(above, fraction.Numerator, barWidth);
                middle.Append('─', barWidth);
                AppendCentered(below, fraction.Denominator, barWidth);
            }
            else
            {
                var columns = TextWidth.Of(part.Text);
                above.Append(' ', columns);
                middle.Append(part.Text);
                below.Append(' ', columns);
            }
            previousWasFraction = part.Fraction.HasValue;
        }

        var center = middle.ToString();
        if (TextWidth.Of(center) > width) return false;
        lines = [above.ToString().TrimEnd(), center, below.ToString().TrimEnd()];
        return true;
    }

    private static string Join(IEnumerable<Part> parts) => string.Concat(parts.Select(static part => part.Text));

    private static void AppendCentered(StringBuilder row, string text, int width)
    {
        var spare = width - TextWidth.Of(text);
        row.Append(' ', spare / 2).Append(text).Append(' ', (spare + 1) / 2);
    }

    private readonly record struct Fraction(string Numerator, string Denominator);
    private readonly record struct Part(string Text, bool Scriptable = true, Fraction? Fraction = null);

    private sealed class Parser(string source)
    {
        private int _position;
        private char Current => _position < source.Length ? source[_position] : '\0';

        public List<Part>? Read()
        {
            var parts = ReadSequence(0);
            return _position == source.Length && parts is { Count: > 0 } ? parts : null;
        }

        private List<Part>? ReadSequence(int depth)
        {
            if (depth > MaxDepth) return null;
            var parts = new List<Part>();
            while (Current is not ('\0' or '}'))
            {
                if (ReadAtom(depth) is not { } part || !ReadScripts(part, depth, out part)) return null;
                parts.Add(part);
            }
            return parts;
        }

        private Part? ReadAtom(int depth)
        {
            if (depth > MaxDepth) return null;
            if (Current == '{')
            {
                var group = ReadGroup(depth);
                return group is null ? null : new(group, Scriptable: group.Length == 1 && char.IsLetterOrDigit(group[0]));
            }
            if (Current == '\\') return ReadCommand(depth);

            var c = Current;
            if (char.IsWhiteSpace(c))
            {
                _position++;
                return new(" ", Scriptable: false);
            }
            if (!char.IsLetterOrDigit(c) && !"+-=*/.,:;!?()[]'|<>".Contains(c)) return null;
            _position++;
            return new(c.ToString(), Scriptable: char.IsLetterOrDigit(c) || c is ')' or ']');
        }

        private Part? ReadCommand(int depth)
        {
            var start = ++_position;
            while (char.IsAsciiLetter(Current)) _position++;
            if (start == _position)
            {
                var escaped = Current;
                if (!"%#&{}_$ ,;:!|".Contains(escaped)) return null;
                _position++;
                return new(escaped switch
                {
                    ',' or ';' or ':' => " ", '!' => "", '|' => "‖", _ => escaped.ToString()
                }, Scriptable: false);
            }
            var name = source[start.._position];
            if (Symbols.TryGetValue(name, out var symbol)) return new(symbol);
            if (Delimiter(name) is { } delimiter) return new(delimiter);
            if (name is "left" or "right" or "bigl" or "bigr") return ReadSizedDelimiter();
            if (name is "quad" or "qquad") return new(" ", Scriptable: false);

            var allowAtom = name is "hat" or "widehat" or "bar" or "tilde" or "widetilde" or "vec" or "dot" or "ddot"
                or "mathcal" or "mathrm" or "mathbf" or "mathit";
            var argument = ReadGroup(depth, allowAtom);
            if (string.IsNullOrWhiteSpace(argument)) return null;
            switch (name)
            {
                case "sqrt": return new($"√({argument})", Scriptable: false);
                case "frac" or "dfrac" or "tfrac":
                    if (depth != 0) return null;
                    var denominator = ReadGroup(depth);
                    if (string.IsNullOrWhiteSpace(denominator)) return null;
                    return new($"(({argument})/({denominator}))", Scriptable: false, new(argument, denominator));
                case "mathbb":
                    const string letters = "CNHPQRZ";
                    const string blackboard = "ℂℕℍℙℚℝℤ";
                    return argument.Length == 1 && letters.IndexOf(argument[0]) is var index && index >= 0
                        ? new(blackboard[index].ToString()) : null;
                case "mathcal":
                    if (!argument.All(char.IsAsciiLetterUpper)) return null;
                    var calligraphic = new StringBuilder(argument.Length * 2);
                    foreach (var letter in argument) calligraphic.Append(CalligraphicLetters[letter - 'A'].ToString());
                    return new(calligraphic.ToString(), Scriptable: argument.Length == 1);
                case "mathrm" or "mathbf" or "mathit":
                    return new(argument, Scriptable: IsSingleGrapheme(argument));
                case "hat" or "widehat" or "bar" or "tilde" or "widetilde" or "vec" or "dot" or "ddot":
                    if (!IsSingleGrapheme(argument) || TextWidth.Of(argument) == 0) return null;
                    var mark = name switch
                    {
                        "hat" or "widehat" => '\u0302', "bar" => '\u0304',
                        "tilde" or "widetilde" => '\u0303', "vec" => '\u20d7',
                        "dot" => '\u0307', _ => '\u0308',
                    };
                    return new(argument + mark);
                default: return null;
            }
        }

        private Part? ReadSizedDelimiter()
        {
            while (char.IsWhiteSpace(Current)) _position++;
            var symbol = Current;
            if (symbol is '.' or '(' or ')' or '[' or ']' or '|' or '<' or '>')
            {
                _position++;
                return symbol switch
                {
                    '.' => new("", Scriptable: false), '<' => new("⟨"), '>' => new("⟩"), _ => new(symbol.ToString())
                };
            }
            if (symbol != '\\') return null;

            var start = ++_position;
            while (char.IsAsciiLetter(Current)) _position++;
            if (_position == start && Current is '{' or '}' or '|') _position++;
            return Delimiter(source[start.._position]) is { } delimiter ? new(delimiter) : null;
        }

        private static bool IsSingleGrapheme(string text) => StringInfo.GetNextTextElementLength(text.AsSpan()) == text.Length;

        private string? ReadGroup(int depth, bool allowAtom = false)
        {
            while (char.IsWhiteSpace(Current)) _position++;
            if (Current != '{') return allowAtom ? ReadAtom(depth + 1)?.Text : null;
            _position++;
            var parts = ReadSequence(depth + 1);
            if (parts is null || Current != '}') return null;
            _position++;
            return Join(parts);
        }

        private static string FormatScript(string raw, bool super)
        {
            var from = super ? SuperSource : SubSource;
            var to = super ? SuperChars : SubChars;
            var script = new StringBuilder(raw.Length);
            foreach (var symbol in raw)
            {
                var index = from.IndexOf(symbol);
                // Keep the scope explicit when the terminal alphabet has no matching symbol.
                if (index < 0) return (super ? "^{" : "_{") + raw + "}";
                script.Append(to[index]);
            }
            return script.ToString();
        }

        private bool ReadScripts(Part atom, int depth, out Part result)
        {
            result = atom;
            bool hasSuper = false, hasSub = false;
            while (Current is '^' or '_')
            {
                var super = Current == '^';
                if (!atom.Scriptable || (super ? hasSuper : hasSub)) return false;
                _position++;
                while (char.IsWhiteSpace(Current)) _position++;
                var grouped = Current == '{';
                var raw = grouped ? ReadGroup(depth) : ReadAtom(depth + 1)?.Text;
                if (!grouped && char.IsLetterOrDigit(Current)) return false;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                result = result with { Text = result.Text + FormatScript(raw, super) };
                hasSuper |= super;
                hasSub |= !super;
            }
            return true;
        }
    }
}
