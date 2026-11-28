using System.Text;

namespace SeekClaw.Runtime.Ssh;

public static class SshAuthMode
{
    public const string None = "none";
    public const string IdentityFile = "identityFile";
    public const string Password = "password";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        IdentityFile => IdentityFile,
        Password => Password,
        _ => None,
    };
}

public sealed class SshConnectionProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string AuthMode { get; set; } = SshAuthMode.None;
    public string? IdentityFile { get; set; }
    public string? Password { get; set; }
    public string? ConfigFile { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SshConfigHostEntry
{
    public string Alias { get; set; } = "";
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int? Port { get; set; }
    public string? IdentityFile { get; set; }
}

public sealed class SshWorkspaceTarget
{
    public required string ConnectionId { get; init; }
    public required string DisplayName { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public string AuthMode { get; init; } = SshAuthMode.None;
    public string? IdentityFile { get; init; }
    public string? Password { get; init; }
    public string? ConfigFile { get; init; }
    public required string RemotePath { get; init; }

    public SshConnectionProfile ToProfile() => new()
    {
        Id = ConnectionId,
        Name = DisplayName,
        Host = Host,
        Port = Port,
        AuthMode = AuthMode,
        IdentityFile = IdentityFile,
        Password = Password,
        ConfigFile = ConfigFile,
    };
}

public sealed record SshFsEntry(string Name, string Path, bool IsDirectory, long SizeBytes);

public sealed record SshFsListResult(string Path, string? ParentPath, IReadOnlyList<SshFsEntry> Entries);

public sealed record SshTestResult(bool Ok, string Message, string? RemoteOs = null, string? RemoteHome = null);

public static class SshPath
{
    public const string SchemePrefix = "ssh://";

    public static bool IsSshPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.TrimStart().StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);

    public static string Format(string connectionId, string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var cleanId = connectionId.Trim().ToLowerInvariant();
        var normalized = NormalizeRemotePath(remotePath);
        if (normalized == "/") return $"{SchemePrefix}{cleanId}/";
        if (normalized.StartsWith('/')) return $"{SchemePrefix}{cleanId}{normalized}";
        return $"{SchemePrefix}{cleanId}/{normalized}";
    }

    public static bool TryParse(string? uri, out string connectionId, out string remotePath)
    {
        connectionId = "";
        remotePath = "";
        if (!IsSshPath(uri)) return false;

        var raw = uri!.Trim();
        var afterScheme = raw[SchemePrefix.Length..];
        var slashIdx = afterScheme.IndexOf('/');
        if (slashIdx < 0)
        {
            connectionId = afterScheme.Trim().ToLowerInvariant();
            remotePath = "~";
            return connectionId.Length > 0;
        }

        connectionId = afterScheme[..slashIdx].Trim().ToLowerInvariant();
        if (connectionId.Length == 0) return false;

        var rawRemote = afterScheme[slashIdx..];
        if (rawRemote.StartsWith("/~", StringComparison.Ordinal)
            && (rawRemote.Length == 2 || rawRemote[2] == '/'))
        {
            remotePath = NormalizeRemotePath(rawRemote[1..]);
        }
        else
        {
            remotePath = NormalizeRemotePath(rawRemote);
        }
        return true;
    }

    public static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "~";
        var raw = path.Trim().Replace('\\', '/');
        if (raw == "/" || raw == "~") return raw;

        var isAbsolute = raw.StartsWith('/');
        var isHomeRelative = !isAbsolute && (raw == "~" || raw.StartsWith("~/", StringComparison.Ordinal));
        var body = isAbsolute ? raw[1..] : isHomeRelative ? (raw.Length > 2 ? raw[2..] : "") : raw;

        var stack = new List<string>();
        foreach (var part in body.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                    stack.RemoveAt(stack.Count - 1);
                else if (!isAbsolute && !isHomeRelative)
                    stack.Add("..");
                continue;
            }
            stack.Add(part);
        }

        if (isAbsolute) return stack.Count == 0 ? "/" : "/" + string.Join('/', stack);
        if (isHomeRelative) return stack.Count == 0 ? "~" : "~/" + string.Join('/', stack);
        return stack.Count == 0 ? "." : string.Join('/', stack);
    }

    public static string ResolveRemotePath(string workspaceRemoteRoot, string? requestedPath)
    {
        var root = NormalizeRemotePath(workspaceRemoteRoot);
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.Trim() == ".")
            return root;

        var req = requestedPath.Trim().Replace('\\', '/');
        if (req.StartsWith('/') || req == "~" || req.StartsWith("~/", StringComparison.Ordinal))
            return NormalizeRemotePath(req);

        var combined = root == "/"
            ? "/" + req
            : root.TrimEnd('/') + "/" + req;
        return NormalizeRemotePath(combined);
    }

    public static string GetDisplayName(string remotePath, string fallbackHostName)
    {
        var normalized = NormalizeRemotePath(remotePath);
        if (normalized == "/" || normalized == "~" || normalized == ".")
            return string.IsNullOrWhiteSpace(fallbackHostName) ? "SSH" : fallbackHostName.Trim();

        var lastSlash = normalized.LastIndexOf('/');
        var tail = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        return string.IsNullOrWhiteSpace(tail) ? fallbackHostName : tail;
    }

    public static string? ComputeParentRemotePath(string path)
    {
        var normalized = NormalizeRemotePath(path);
        if (normalized == "/" || normalized == "~" || normalized == ".") return null;
        var idx = normalized.LastIndexOf('/');
        if (idx < 0) return null;
        if (idx == 0) return "/";
        return normalized[..idx];
    }

    public static string RelativeRemotePath(string workspaceRoot, string targetPath)
    {
        var root = NormalizeRemotePath(workspaceRoot).TrimEnd('/');
        var target = NormalizeRemotePath(targetPath);
        if (string.Equals(root, target, StringComparison.Ordinal)) return ".";
        if (root.Length == 0 && target.StartsWith('/')) return target[1..];
        if (target.StartsWith(root + "/", StringComparison.Ordinal))
            return target[(root.Length + 1)..];
        return target;
    }

    public static string ShellQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        sb.Append('\'');
        foreach (var ch in value)
        {
            if (ch == '\'') sb.Append("'\\''");
            else sb.Append(ch);
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
