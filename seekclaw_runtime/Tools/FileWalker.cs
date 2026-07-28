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
        private readonly List<(Regex Regex, bool DirOnly)> _rules = [];

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

                    var dirOnly = line.EndsWith('/');
                    var pattern = line.TrimEnd('/');

                    if (pattern.StartsWith('/')) pattern = pattern[1..];
                    if (pattern.Length == 0) continue;

                    var regexPattern = "^" + Regex.Escape(pattern)
                        .Replace(@"\*\*", ".*")
                        .Replace(@"\*", @"[^/]*")
                        .Replace(@"\?", ".") + "(?:$|/)";

                    try
                    {
                        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
                        _rules.Add((regex, dirOnly));
                    }
                    catch (ArgumentException) { }
                }
            }
            catch (IOException) { }
        }

        public bool IsIgnored(string fullPath, bool isDir)
        {
            if (_rules.Count == 0) return false;
            var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

            foreach (var (regex, dirOnly) in _rules)
            {
                if (dirOnly && !isDir) continue;
                try
                {
                    if (regex.IsMatch(relative)) return true;
                }
                catch (RegexMatchTimeoutException) { }
            }
            return false;
        }
    }
}
