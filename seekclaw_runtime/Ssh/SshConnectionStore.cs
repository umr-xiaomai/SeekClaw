using System.Text.Json;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Ssh;

public interface ISshConnectionStore
{
    IReadOnlyList<SshConnectionProfile> List();
    SshConnectionProfile? Get(string id);
    SshConnectionProfile Upsert(SshConnectionProfile input, bool preserveExistingPassword = true);
    bool Remove(string id);
}

public sealed class SshConnectionStore(string? filePath = null) : ISshConnectionStore
{
    private readonly string _filePath = Path.GetFullPath(filePath ?? SeekClawPaths.SshConnectionsFile);
    private readonly Lock _gate = new();

    public IReadOnlyList<SshConnectionProfile> List()
    {
        lock (_gate)
        {
            return LoadUnsafe()
                .OrderByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public SshConnectionProfile? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var clean = id.Trim();
        lock (_gate)
        {
            return LoadUnsafe().FirstOrDefault(x => string.Equals(x.Id, clean, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SshConnectionProfile Upsert(SshConnectionProfile input, bool preserveExistingPassword = true)
    {
        if (string.IsNullOrWhiteSpace(input.Host))
            throw new ArgumentException("SSH host is required.", nameof(input));

        var host = input.Host.Trim();
        var port = input.Port is >= 1 and <= 65535 ? input.Port : 22;
        var authMode = SshAuthMode.Normalize(input.AuthMode);
        var name = string.IsNullOrWhiteSpace(input.Name) ? host : input.Name.Trim();
        var identityFile = string.IsNullOrWhiteSpace(input.IdentityFile) ? null : input.IdentityFile.Trim();
        var configFile = string.IsNullOrWhiteSpace(input.ConfigFile) ? null : input.ConfigFile.Trim();

        lock (_gate)
        {
            var items = LoadUnsafe();
            var id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N")[..12] : input.Id.Trim().ToLowerInvariant();
            var existingIdx = items.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow;

            string? password;
            if (authMode != SshAuthMode.Password)
            {
                password = null;
            }
            else if (!string.IsNullOrEmpty(input.Password))
            {
                password = input.Password;
            }
            else if (preserveExistingPassword && existingIdx >= 0)
            {
                password = items[existingIdx].Password;
            }
            else
            {
                password = null;
            }

            var saved = new SshConnectionProfile
            {
                Id = id,
                Name = name,
                Host = host,
                Port = port,
                AuthMode = authMode,
                IdentityFile = authMode == SshAuthMode.IdentityFile ? identityFile : null,
                Password = password,
                ConfigFile = configFile,
                CreatedAt = existingIdx >= 0 ? items[existingIdx].CreatedAt : now,
                UpdatedAt = now,
            };

            if (existingIdx >= 0) items[existingIdx] = saved;
            else items.Add(saved);

            SaveUnsafe(items);
            return saved;
        }
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var clean = id.Trim();
        lock (_gate)
        {
            var items = LoadUnsafe();
            var removed = items.RemoveAll(x => string.Equals(x.Id, clean, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveUnsafe(items);
            return removed;
        }
    }

    private List<SshConnectionProfile> LoadUnsafe()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, SeekClawJsonContext.Default.ListSshConnectionProfile) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void SaveUnsafe(List<SshConnectionProfile> items)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(items, SeekClawJsonContext.Default.ListSshConnectionProfile);
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static string DefaultSshConfigPath =>
        Path.Combine(SeekClawPaths.HomeDir, ".ssh", "config");

    public static IReadOnlyList<SshConfigHostEntry> ParseSshConfigHosts(string? configFilePath = null)
    {
        var resolved = ResolveConfigPath(configFilePath);
        if (!File.Exists(resolved)) return [];

        string[] lines;
        try { lines = File.ReadAllLines(resolved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        var result = new List<SshConfigHostEntry>();
        SshConfigHostEntry? current = null;

        void Flush()
        {
            if (current is null) return;
            if (!string.IsNullOrWhiteSpace(current.Alias))
                result.Add(current);
            current = null;
        }

        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            var parts = SplitKeyValue(line);
            if (parts is null) continue;
            var (key, value) = parts.Value;

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var alias = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(token => !token.Contains('*') && !token.Contains('?') && !token.StartsWith('!'));
                if (alias is not null)
                    current = new SshConfigHostEntry { Alias = alias };
                continue;
            }

            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            if (current is null) continue;

            if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase))
                current.HostName = Unquote(value);
            else if (key.Equals("User", StringComparison.OrdinalIgnoreCase))
                current.User = Unquote(value);
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p) && p is >= 1 and <= 65535)
                current.Port = p;
            else if (key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
                current.IdentityFile = Unquote(value);
        }

        Flush();
        return result;
    }

    private static string ResolveConfigPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultSshConfigPath;
        var trimmed = path.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(SeekClawPaths.HomeDir, trimmed[2..]));
        return Path.GetFullPath(trimmed);
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == '#' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static (string Key, string Value)? SplitKeyValue(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == ' ' || ch == '\t' || ch == '=')
            {
                var key = line[..i].Trim();
                var value = line[(i + 1)..].TrimStart(' ', '\t', '=').Trim();
                return key.Length > 0 && value.Length > 0 ? (key, value) : null;
            }
        }
        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
