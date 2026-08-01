using System.Text;
using System.Text.RegularExpressions;

namespace SeekClaw.Runtime.Tools;

/// <summary>Shared directory traversal with standard ignore rules (VCS, build output, caches, .gitignore, .seekclawignore).</summary>
public static class FileWalker
{
    public static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "out",
        ".cache", ".session", ".seekclaw", ".vs", ".idea", ".vscode", "target",
        "__pycache__", ".venv", "venv", ".next", ".nuxt", "Library", "Temp", "packages",
    };

    public static IEnumerable<string> EnumerateFiles(string root, int maxFiles = 50_000)
    {
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        var ignoreMatcher = IgnoreMatcher.ForRoot(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> subdirs;
            IEnumerable<string> files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var file in files)
            {
                if (ignoreMatcher.IsIgnored(file, isDir: false)) continue;
                if (++count > maxFiles) yield break;
                yield return file;
            }

            foreach (var subdir in subdirs)
            {
                var name = Path.GetFileName(subdir);
                if (IgnoredDirectories.Contains(name)) continue;
                if (ignoreMatcher.IsIgnored(subdir, isDir: true)) continue;
                stack.Push(subdir);
            }
        }
    }

    /// <summary>Cheap binary sniff: NUL byte in the first 8KB.</summary>
    public static bool IsProbablyBinary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> buffer = stackalloc byte[8192];
            var read = stream.Read(buffer);
            return buffer[..read].IndexOf((byte)0) >= 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public sealed class IgnoreMatcher
    {
        private readonly string _root;
        private readonly List<(Regex Regex, bool DirOnly, bool Negated, bool Anchored)> _rules = [];

        private IgnoreMatcher(string root)
        {
            _root = root;
            LoadRules(Path.Combine(root, ".gitignore"));
            LoadRules(Path.Combine(root, ".seekclawignore"));
        }

        public static IgnoreMatcher ForRoot(string root) => new(root);

        private void LoadRules(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                foreach (var rawLine in File.ReadLines(filePath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;

                    var negated = line[0] == '!';
                    if (negated) line = line[1..].TrimStart();
                    else if (line.StartsWith(@"\#") || line.StartsWith(@"\!")) line = line[1..];
                    if (line.Length == 0) continue;

                    var dirOnly = line.EndsWith('/');
                    line = line.TrimEnd('/');

                    // A leading slash anchors the pattern to the ignore-file root.
                    var anchored = line.StartsWith('/');
                    if (anchored) line = line[1..];
                    if (line.Length == 0) continue;

                    // Git semantics: a pattern without a slash matches the basename at
                    // any depth; a pattern containing a slash is root-anchored.
                    anchored |= line.Contains('/');

                    var regexPattern = "^" + Translate(line) + "(?:$|/)";
                    try
                    {
                        var regex = new Regex(
                            regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                            TimeSpan.FromMilliseconds(200));
                        _rules.Add((regex, dirOnly, negated, anchored));
                    }
                    catch (ArgumentException) { }
                }
            }
            catch (IOException) { }
        }

        /// <summary>Translates one gitignore pattern (without a leading '!' or '/') into regex source.</summary>
        private static string Translate(string pattern)
        {
            var sb = new StringBuilder(pattern.Length + 8);
            var i = 0;
            while (i < pattern.Length)
            {
                var ch = pattern[i];
                if (ch == '*')
                {
                    var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (isDouble)
                    {
                        var prevSlash = i == 0 || pattern[i - 1] == '/';
                        var nextSlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                        var atEnd = i + 2 == pattern.Length;

                        if (i == 0 && nextSlash)
                        {
                            // leading "**/" matches any number of directory levels
                            sb.Append("(?:[^/]+/)*");
                            i += 3;
                            continue;
                        }
                        if (prevSlash && atEnd)
                        {
                            // trailing "/**" matches the directory and everything below
                            sb.Append("(?:/.*)?");
                            i += 2;
                            continue;
                        }
                        if (prevSlash && nextSlash)
                        {
                            // "a/**/b" — zero or more directory levels between segments
                            sb.Append("(?:[^/]+/)*");
                            i += 3;
                            continue;
                        }
                        // Any other "**" behaves like a single "*" (no crossing '/')
                        sb.Append("[^/]*");
                        i += 2;
                        continue;
                    }

                    sb.Append("[^/]*");
                    i++;
                }
                else if (ch == '?')
                {
                    sb.Append("[^/]");
                    i++;
                }
                else if (ch == '[')
                {
                    var end = pattern.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        i++;
                        continue;
                    }
                    var cls = pattern[(i + 1)..end];
                    sb.Append('[');
                    if (cls.StartsWith('!')) { sb.Append('^'); cls = cls[1..]; }
                    foreach (var c in cls)
                    {
                        if (c is '\\' or ']' or '^' or '[') sb.Append('\\');
                        sb.Append(c);
                    }
                    sb.Append(']');
                    i = end + 1;
                }
                else
                {
                    sb.Append(Regex.Escape(ch.ToString()));
                    i++;
                }
            }
            return sb.ToString();
        }

        public bool IsIgnored(string fullPath, bool isDir)
        {
            if (_rules.Count == 0) return false;
            var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
            var slash = relative.LastIndexOf('/');
            var basename = slash < 0 ? relative : relative[(slash + 1)..];

            // Last matching rule wins (git semantics), including '!' negation rules.
            var ignored = false;
            foreach (var (regex, dirOnly, negated, anchored) in _rules)
            {
                if (dirOnly && !isDir) continue;
                var target = anchored ? relative : basename;
                try
                {
                    if (!regex.IsMatch(target)) continue;
                }
                catch (RegexMatchTimeoutException) { continue; }
                ignored = !negated;
            }
            return ignored;
        }
    }
}
