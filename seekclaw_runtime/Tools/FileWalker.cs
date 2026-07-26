namespace SeekClaw.Runtime.Tools;

/// <summary>Shared directory traversal with standard ignore rules (VCS, build output, caches).</summary>
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
                if (++count > maxFiles) yield break;
                yield return file;
            }

            foreach (var subdir in subdirs)
            {
                var name = Path.GetFileName(subdir);
                if (!IgnoredDirectories.Contains(name))
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
}
