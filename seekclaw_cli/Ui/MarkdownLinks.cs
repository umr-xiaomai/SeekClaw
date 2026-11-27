using System.Text.RegularExpressions;

namespace SeekClaw.Cli.Ui;

/// <summary>Terminal hyperlink policy and lexical display of source references.</summary>
internal static partial class MarkdownLinks
{
    internal const string OpenPrefix = "\x1b]8;;";
    internal const string Terminator = "\x1b\\";
    internal const string Close = OpenPrefix + Terminator;
    private const int MaxDestinationLength = 8192;

    internal static bool CanOpenWebLinks { get; } =
        !Console.IsOutputRedirected && SupportsHyperlinks(Environment.GetEnvironmentVariable);

    internal static bool SupportsHyperlinks(Func<string, string?> environment)
    {
        var terminal = environment("TERM");
        if (environment("TMUX") is not null || environment("STY") is not null ||
            terminal is "dumb" || terminal?.StartsWith("screen", StringComparison.Ordinal) == true ||
            terminal?.StartsWith("tmux", StringComparison.Ordinal) == true)
            return false;

        return environment("WT_SESSION") is not null || environment("VTE_VERSION") is not null ||
            environment("KONSOLE_VERSION") is not null || environment("KITTY_WINDOW_ID") is not null ||
            terminal is "xterm-kitty" or "xterm-ghostty" ||
            environment("TERM_PROGRAM") is "vscode" or "iTerm.app" or "WezTerm" or "ghostty" or "Alacritty";
    }

    internal static string? WebDestination(string destination)
    {
        if (destination.Length is 0 or > MaxDestinationLength ||
            destination.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(destination, UriKind.Absolute, out var uri) ||
            !(uri.Scheme is "http" or "https") || uri.Host.Length == 0)
            return null;

        // Use an escaped absolute URL so the payload cannot terminate the OSC sequence.
        var escaped = uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        return escaped.Length <= MaxDestinationLength ? escaped : null;
    }

    internal static bool IsSafeSequence(string sequence)
    {
        if (sequence == Close) return true;
        if (!sequence.StartsWith(OpenPrefix, StringComparison.Ordinal) ||
            !sequence.EndsWith(Terminator, StringComparison.Ordinal)) return false;
        var destination = sequence[OpenPrefix.Length..^Terminator.Length];
        return WebDestination(destination) == destination;
    }

    internal static string? LocalTarget(string destination, string? workingDirectory)
    {
        if (destination.Length is 0 or > MaxDestinationLength || destination.Any(char.IsControl)) return null;
        string path;
        string suffix = "";
        if (destination.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (uri.Host.Length > 0 && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                path = "//" + uri.Host + (path.StartsWith('/') ? path : "/" + path);
            else if (path.StartsWith('/') && IsWindowsPath(path.AsSpan(1)))
                path = path[1..];
            suffix = NormalizeLocation(uri.Fragment) ?? uri.Fragment;
        }
        else
        {
            var lineSuffix = ColonLocation().Match(destination);
            var pathCandidate = lineSuffix.Success ? destination[..lineSuffix.Index] : destination;
            if (!LooksLikeLocalPath(pathCandidate)) return null;
            var hash = destination.LastIndexOf('#');
            if (hash >= 0 && NormalizeLocation(destination[hash..]) is { } location)
            {
                suffix = location;
                destination = destination[..hash];
            }
            path = Uri.UnescapeDataString(destination).Replace('\\', '/');
        }
        if (path.Length == 0 || path.Any(char.IsControl)) return null;

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            var prefix = workingDirectory.Replace('\\', '/').TrimEnd('/') + "/";
            var comparison = IsWindowsPath(prefix) || prefix.StartsWith("//", StringComparison.Ordinal)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (path.Length > prefix.Length && path.StartsWith(prefix, comparison)) path = path[prefix.Length..];
        }

        return path + suffix;
    }

    private static bool LooksLikeLocalPath(string destination)
    {
        return destination.StartsWith('/') || destination.StartsWith("\\\\", StringComparison.Ordinal) ||
            destination.StartsWith("~/", StringComparison.Ordinal) || IsWindowsPath(destination) ||
            !destination.StartsWith('#') && !HasScheme().IsMatch(destination) &&
            destination.AsSpan().IndexOfAny('/', '\\', '.') >= 0;
    }

    private static bool IsWindowsPath(ReadOnlySpan<char> path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';

    private static string? NormalizeLocation(string fragment)
    {
        if (!LocationFragment().IsMatch(fragment)) return null;
        return ":" + fragment[2..].Replace("-L", "-", StringComparison.Ordinal).Replace('C', ':');
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex HasScheme();

    [GeneratedRegex(@":[0-9]+(?::[0-9]+)?(?:[-–][0-9]+(?::[0-9]+)?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColonLocation();

    [GeneratedRegex(@"\A#L[0-9]+(?:C[0-9]+)?(?:-L[0-9]+(?:C[0-9]+)?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocationFragment();
}
