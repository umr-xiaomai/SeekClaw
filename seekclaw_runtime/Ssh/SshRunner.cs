using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Presentation;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Ssh;

public sealed record SshExecOutput(int ExitCode, string Stdout, string Stderr);

public static class SshRunner
{
    private static readonly TimeSpan DefaultControlTimeout = TimeSpan.FromSeconds(30);

    public static string ResolveSshExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            var systemSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (File.Exists(systemSsh)) return systemSsh;
        }
        return "ssh";
    }

    public static async Task<SshExecOutput> ExecRawAsync(
        SshConnectionProfile profile,
        string remoteCommand,
        string? stdinUtf8 = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("SSH 主机名不能为空。");

        var effectiveTimeout = timeout ?? DefaultControlTimeout;
        var sshExe = ResolveSshExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = sshExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var authMode = SshAuthMode.Normalize(profile.AuthMode);
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=15");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=30");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveCountMax=3");

        if (!string.IsNullOrWhiteSpace(profile.ConfigFile))
        {
            psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add(ExpandHomePath(profile.ConfigFile.Trim()));
        }

        if (profile.Port is > 0 and <= 65535 && profile.Port != 22)
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(profile.Port.ToString());
        }

        if (authMode == SshAuthMode.IdentityFile && !string.IsNullOrWhiteSpace(profile.IdentityFile))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(ExpandHomePath(profile.IdentityFile.Trim()));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("IdentitiesOnly=yes");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
        }
        else if (authMode == SshAuthMode.Password)
        {
            if (string.IsNullOrEmpty(profile.Password))
                throw new InvalidOperationException("当前 SSH 连接配置为密码登录，但尚未设置密码。");

            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("PubkeyAuthentication=no");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("PreferredAuthentications=password,keyboard-interactive");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("NumberOfPasswordPrompts=1");

            var askPassPath = EnsureAskPassScript();
            psi.Environment["SSH_ASKPASS"] = askPassPath;
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            psi.Environment["DISPLAY"] = "seekclaw:0";
            psi.Environment["SEEKCLAW_SSH_SECRET"] = profile.Password;
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
                psi.Environment["SEEKCLAW_ASKPASS_BIN"] = currentExe;
        }
        else
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
        }

        psi.ArgumentList.Add(profile.Host.Trim());
        psi.ArgumentList.Add(remoteCommand);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(effectiveTimeout);

        var stdoutBuf = new HeadTailBuffer(maxBytes: 256 * 1024);
        var stderrBuf = new HeadTailBuffer(maxBytes: 64 * 1024);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动 SSH 客户端: {sshExe}");

        try
        {
            if (!string.IsNullOrEmpty(stdinUtf8))
            {
                var inputBytes = Encoding.UTF8.GetBytes(stdinUtf8);
                await process.StandardInput.BaseStream.WriteAsync(inputBytes, cts.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Ignore broken pipe if remote already rejected auth before consuming stdin.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
        }

        var readOut = DrainStreamAsync(process.StandardOutput.BaseStream, stdoutBuf, cts.Token);
        var readErr = DrainStreamAsync(process.StandardError.BaseStream, stderrBuf, cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"SSH 命令执行超时（{effectiveTimeout.TotalSeconds:0}s）");
        }
        catch (Exception)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new SshExecOutput(
            process.ExitCode,
            stdoutBuf.GetFormattedText(Encoding.UTF8),
            stderrBuf.GetFormattedText(Encoding.UTF8));
    }

    public static async Task<SshTestResult> TestAsync(SshConnectionProfile profile, CancellationToken ct = default)
    {
        try
        {
            var output = await ExecRawAsync(
                profile,
                "printf '__OS__=%s\\n__HOME__=%s\\n' \"$(uname -srm 2>/dev/null || uname -a)\" \"$HOME\"",
                timeout: TimeSpan.FromSeconds(20),
                ct: ct).ConfigureAwait(false);

            if (output.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(output.Stderr) ? output.Stdout : output.Stderr;
                return new SshTestResult(false, string.IsNullOrWhiteSpace(detail) ? $"SSH 连接失败（退出码 {output.ExitCode}）" : detail.Trim());
            }

            string? os = null;
            string? home = null;
            foreach (var line in output.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("__OS__=", StringComparison.Ordinal)) os = line[7..].Trim();
                else if (line.StartsWith("__HOME__=", StringComparison.Ordinal)) home = line[9..].Trim();
            }

            return new SshTestResult(true, "连接成功", os, home);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SshTestResult(false, ex.Message);
        }
    }

    public static async Task<SshFsListResult> ListDirectoryAsync(
        SshConnectionProfile profile,
        string? remotePath,
        bool showHidden,
        CancellationToken ct = default)
    {
        var requested = string.IsNullOrWhiteSpace(remotePath) ? "~" : SshPath.NormalizeRemotePath(remotePath);
        var cdExpr = requested == "~"
            ? "\"$HOME\""
            : requested.StartsWith("~/", StringComparison.Ordinal)
                ? "\"$HOME\"/" + SshPath.ShellQuote(requested[2..])
                : SshPath.ShellQuote(requested);

        var hiddenFlag = showHidden ? "-A " : "";
        var script = $$"""
            sh -c '
              set -e
              TARGET={{cdExpr}}
              cd "$TARGET" 2>/dev/null || { echo "__ERR__:目录不存在或无权限访问: $TARGET" >&2; exit 2; }
              RESOLVED=$(pwd)
              printf "__PWD__=%s\n" "$RESOLVED"
              LC_ALL=C ls -la {{hiddenFlag}}-- 2>/dev/null | while IFS= read -r line; do
                printf "__LS__=%s\n" "$line"
              done
            '
            """;

        var result = await ExecRawAsync(profile, script, timeout: TimeSpan.FromSeconds(25), ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var err = result.Stderr.Trim();
            if (err.StartsWith("__ERR__:", StringComparison.Ordinal)) err = err[8..].Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"无法列出远端目录（退出码 {result.ExitCode}）" : err);
        }

        string resolvedPath = requested;
        var entries = new List<SshFsEntry>();

        foreach (var rawLine in result.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("__PWD__=", StringComparison.Ordinal))
            {
                resolvedPath = line[8..].Trim();
                continue;
            }
            if (!line.StartsWith("__LS__=", StringComparison.Ordinal)) continue;
            var lsLine = line[7..];
            if (lsLine.StartsWith("total ", StringComparison.Ordinal)) continue;
            if (TryParseLsLine(lsLine, resolvedPath, out var entry))
                entries.Add(entry!);
        }

        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new SshFsListResult(resolvedPath, SshPath.ComputeParentRemotePath(resolvedPath), entries);
    }

    public static async Task<string> CreateDirectoryAsync(
        SshConnectionProfile profile,
        string parentPath,
        string name,
        CancellationToken ct = default)
    {
        var cleanName = name.Trim();
        if (string.IsNullOrWhiteSpace(cleanName) || cleanName == "." || cleanName == ".."
            || cleanName.Contains('/') || cleanName.Contains('\\') || cleanName.Any(char.IsControl))
        {
            throw new ArgumentException("请输入有效的文件夹名称");
        }

        var normalizedParent = string.IsNullOrWhiteSpace(parentPath) ? "~" : SshPath.NormalizeRemotePath(parentPath);
        var fullTarget = SshPath.ResolveRemotePath(normalizedParent, cleanName);
        var targetExpr = fullTarget.StartsWith("~/", StringComparison.Ordinal)
            ? "\"$HOME\"/" + SshPath.ShellQuote(fullTarget[2..])
            : SshPath.ShellQuote(fullTarget);

        var script = $"sh -c 'mkdir -p {targetExpr} && cd {targetExpr} && pwd'";
        var res = await ExecRawAsync(profile, script, timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(res.Stderr) ? "创建远端目录失败" : res.Stderr.Trim());

        var created = res.Stdout.Trim().Split('\n').LastOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(created) ? fullTarget : created;
    }

    private static bool TryParseLsLine(string line, string parentDir, out SshFsEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line) || line.Length < 10) return false;
        var kindChar = line[0];
        if (kindChar is not ('d' or '-' or 'l')) return false;

        // Standard POSIX ls -la: perms links owner group size month day time/year name
        var tokens = line.Split([' ', '\t'], 9, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 9) return false;
        var rawName = tokens[8].Trim();
        if (kindChar == 'l')
        {
            var arrowIdx = rawName.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0) rawName = rawName[..arrowIdx];
        }
        if (rawName is "." or ".." || string.IsNullOrWhiteSpace(rawName)) return false;

        var isDir = kindChar == 'd';
        long size = 0;
        if (!isDir) _ = long.TryParse(tokens[4], out size);
        var fullPath = parentDir == "/" ? "/" + rawName : parentDir.TrimEnd('/') + "/" + rawName;
        entry = new SshFsEntry(rawName, fullPath, isDir, size);
        return true;
    }

    private static string ExpandHomePath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(SeekClawPaths.HomeDir, path[2..]));
        return path;
    }

    private static string EnsureAskPassScript()
    {
        var tmpDir = SeekClawPaths.GetTemporaryDirectory();
        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(tmpDir, "ssh-askpass.cmd");
            const string body = "@echo off\r\nif defined SEEKCLAW_ASKPASS_BIN (\r\n  \"%SEEKCLAW_ASKPASS_BIN%\" --ssh-askpass\r\n) else (\r\n  powershell -NoProfile -Command \"[Console]::Out.Write($env:SEEKCLAW_SSH_SECRET)\"\r\n)\r\n";
            if (!File.Exists(cmdPath) || File.ReadAllText(cmdPath) != body)
                File.WriteAllText(cmdPath, body, new UTF8Encoding(false));
            return cmdPath;
        }
        else
        {
            var shPath = Path.Combine(tmpDir, "ssh-askpass.sh");
            const string body = "#!/bin/sh\nprintf '%s' \"$SEEKCLAW_SSH_SECRET\"\n";
            if (!File.Exists(shPath) || File.ReadAllText(shPath) != body)
                File.WriteAllText(shPath, body, new UTF8Encoding(false));
            File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return shPath;
        }
    }

    private static async Task DrainStreamAsync(Stream stream, HeadTailBuffer buffer, CancellationToken ct)
    {
        var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                buffer.PushChunk(chunk, 0, read);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}

internal static class SshRemoteToolExecutor
{
    public static async Task<ToolResult> ReadFileAsync(
        SshWorkspaceTarget target, string requestedPath, int offset, int limit, ToolContext context, CancellationToken ct)
    {
        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var script = $"sh -c 'test -f {pathExpr} || {{ echo \"__MISSING__\" >&2; exit 2; }}; base64 < {pathExpr} | tr -d \"\\n\\r\"'";
        var res = await SshRunner.ExecRawAsync(target.ToProfile(), script, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return ToolResult.Fail(res.Stderr.Contains("__MISSING__", StringComparison.Ordinal)
                ? $"Remote file not found: {remotePath}"
                : $"Failed to read remote file {remotePath}: {res.Stderr.Trim()}");
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(res.Stdout.Trim()); }
        catch (FormatException) { return ToolResult.Fail($"Failed to decode remote file contents: {remotePath}"); }

        var decoded = DecodeText(bytes);
        var allLines = decoded.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var selected = allLines.Skip(offset - 1).Take(limit).ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < selected.Count; i++)
        {
            var line = selected[i].Length > 2000 ? selected[i][..2000] + "…" : selected[i];
            sb.Append(offset + i).Append('\t').AppendLine(line);
        }
        if (sb.Length > 0)
            context.Events.Publish(new ToolCallProgressEvent(context.CallId, sb.ToString()));

        var output = sb.Length == 0 ? "(empty file)" : context.Truncate(sb.ToString(), "file");
        var relative = SshPath.RelativeRemotePath(target.RemotePath, remotePath);
        var displayUri = SshPath.Format(target.ConnectionId, remotePath);
        return ToolResult.Ok(output, $"Read {selected.Count} lines from {relative}") with { FilePath = displayUri };
    }

    public static async Task<ToolResult> WriteFileAsync(
        SshWorkspaceTarget target, string requestedPath, string content, ToolContext context, CancellationToken ct)
    {
        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var probe = await SshRunner.ExecRawAsync(
            target.ToProfile(),
            $"sh -c 'if [ -d {pathExpr} ]; then exit 3; fi; if [ -f {pathExpr} ]; then base64 < {pathExpr} | tr -d \"\\n\\r\"; fi'",
            timeout: TimeSpan.FromSeconds(25),
            ct: ct).ConfigureAwait(false);

        if (probe.ExitCode == 3)
            return ToolResult.Fail($"Cannot write to '{remotePath}' because it is a directory.");

        var existed = probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.Stdout);
        var oldFile = existed ? DecodeText(Convert.FromBase64String(probe.Stdout.Trim())) : new DecodedTextFile("", new UTF8Encoding(false), []);

        var bodyBytes = oldFile.Encoding.GetBytes(content);
        var fullBytes = oldFile.Preamble.Length > 0
            ? [.. oldFile.Preamble, .. bodyBytes]
            : bodyBytes;
        var b64 = Convert.ToBase64String(fullBytes);

        var writeScript = $"sh -c 'DIR=$(dirname {pathExpr}); mkdir -p \"$DIR\" && TMP=\"$DIR/.seekclaw.$$.tmp\" && (base64 -d > \"$TMP\" 2>/dev/null || base64 --decode > \"$TMP\") && mv -f \"$TMP\" {pathExpr}'";
        var writeRes = await SshRunner.ExecRawAsync(target.ToProfile(), writeScript, stdinUtf8: b64, timeout: TimeSpan.FromSeconds(45), ct: ct).ConfigureAwait(false);
        if (writeRes.ExitCode != 0)
            return ToolResult.Fail($"Failed to write remote file {remotePath}: {writeRes.Stderr.Trim()}");

        var relative = SshPath.RelativeRemotePath(target.RemotePath, remotePath);
        var lineCount = content.Count(c => c == '\n') + 1;
        var diff = DiffUtil.Unified(oldFile.Text, content, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        var displayUri = SshPath.Format(target.ConnectionId, remotePath);
        return ToolResult.Ok(
            existed ? $"Overwrote {relative}" : $"Created {relative}",
            $"{(existed ? "Updated" : "Created")} {relative} ({lineCount} lines)") with { Diff = diff, FilePath = displayUri };
    }

    public static async Task<ToolResult> EditFileAsync(
        SshWorkspaceTarget target, string requestedPath, string oldString, string newString, bool replaceAll, ToolContext context, CancellationToken ct)
    {
        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var readRes = await SshRunner.ExecRawAsync(
            target.ToProfile(),
            $"sh -c 'test -f {pathExpr} || exit 2; base64 < {pathExpr} | tr -d \"\\n\\r\"'",
            timeout: TimeSpan.FromSeconds(30),
            ct: ct).ConfigureAwait(false);

        if (readRes.ExitCode != 0)
            return ToolResult.Fail($"Remote file not found: {remotePath}");

        var file = DecodeText(Convert.FromBase64String(readRes.Stdout.Trim()));
        var text = file.Text;
        var newline = TextFileIO.DetectDominantNewline(text);
        var normalizedText = TextFileIO.NormalizeNewlines(text);
        var normalizedOld = TextFileIO.NormalizeNewlines(oldString);
        var normalizedNew = TextFileIO.NormalizeNewlines(newString);
        var occurrences = EditFileTool.CountOccurrences(normalizedText, normalizedOld);
        if (occurrences == 0)
            return ToolResult.Fail($"old_string not found in {remotePath}. Read the file again — the content may have changed.");
        if (occurrences > 1 && !replaceAll)
            return ToolResult.Fail($"old_string matches {occurrences} locations in {remotePath}. Provide more surrounding context to make it unique, or set replace_all.");

        var updatedNormalized = replaceAll
            ? normalizedText.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
            : EditFileTool.ReplaceFirst(normalizedText, normalizedOld, normalizedNew);
        var updated = TextFileIO.RestoreNewlines(updatedNormalized, newline);

        var bodyBytes = file.Encoding.GetBytes(updated);
        var fullBytes = file.Preamble.Length > 0 ? [.. file.Preamble, .. bodyBytes] : bodyBytes;
        var writeScript = $"sh -c 'DIR=$(dirname {pathExpr}); TMP=\"$DIR/.seekclaw.$$.tmp\" && (base64 -d > \"$TMP\" 2>/dev/null || base64 --decode > \"$TMP\") && mv -f \"$TMP\" {pathExpr}'";
        var writeRes = await SshRunner.ExecRawAsync(target.ToProfile(), writeScript, stdinUtf8: Convert.ToBase64String(fullBytes), timeout: TimeSpan.FromSeconds(45), ct: ct).ConfigureAwait(false);
        if (writeRes.ExitCode != 0)
            return ToolResult.Fail($"Failed to write remote file {remotePath}: {writeRes.Stderr.Trim()}");

        var relative = SshPath.RelativeRemotePath(target.RemotePath, remotePath);
        var diff = DiffUtil.Unified(text, updated, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        var summary = replaceAll && occurrences > 1
            ? $"Replaced {occurrences} occurrences in {relative}"
            : $"Edited {relative}";
        return ToolResult.Ok(summary, summary) with { Diff = diff, FilePath = SshPath.Format(target.ConnectionId, remotePath) };
    }

    public static async Task<ToolResult> ListDirAsync(
        SshWorkspaceTarget target, string? requestedPath, int depth, ToolContext context, CancellationToken ct)
    {
        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var script = $"sh -c 'test -d {pathExpr} || exit 2; find {pathExpr} -maxdepth {depth} \\( -name .git -o -name node_modules -o -name bin -o -name obj -o -name __pycache__ \\) -prune -o -print | head -n 501'";
        var res = await SshRunner.ExecRawAsync(target.ToProfile(), script, timeout: TimeSpan.FromSeconds(25), ct: ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
            return ToolResult.Fail($"Directory not found: {remotePath}");

        var lines = res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(l => SshPath.RelativeRemotePath(remotePath, l.TrimEnd('\r')))
            .Where(l => l != ".")
            .ToList();
        var output = lines.Count == 0 ? "(empty directory)" : context.Truncate(string.Join('\n', lines), "listing");
        return ToolResult.Ok(output, $"Listed {lines.Count} entries under {SshPath.RelativeRemotePath(target.RemotePath, remotePath)}");
    }

    public static async Task<ToolResult> GlobAsync(
        SshWorkspaceTarget target, string? requestedPath, string pattern, ToolContext context, CancellationToken ct)
    {
        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var script = $"sh -c 'test -d {pathExpr} || exit 2; find {pathExpr} \\( -name .git -o -name node_modules -o -name bin -o -name obj -o -name __pycache__ \\) -prune -o -type f -print | head -n 4000'";
        var res = await SshRunner.ExecRawAsync(target.ToProfile(), script, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
            return ToolResult.Fail($"Directory not found: {remotePath}");

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(pattern);
        var matches = res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => SshPath.RelativeRemotePath(remotePath, line.TrimEnd('\r')))
            .Where(rel => !string.IsNullOrWhiteSpace(rel) && matcher.Match(rel).HasMatches)
            .Take(200)
            .ToList();

        var output = matches.Count == 0 ? "No files matched." : string.Join('\n', matches);
        return ToolResult.Ok(context.Truncate(output, "matches"), $"{matches.Count} files matched {pattern}");
    }

    public static async Task<ToolResult> GrepAsync(
        SshWorkspaceTarget target, string? requestedPath, string patternText, string? globPattern, int maxResults, ToolContext context, CancellationToken ct)
    {
        try { _ = new Regex(patternText); }
        catch (ArgumentException ex) { return ToolResult.Fail($"Invalid regex: {ex.Message}"); }

        var remotePath = SshPath.ResolveRemotePath(target.RemotePath, requestedPath);
        var pathExpr = RemotePathExpr(remotePath);
        var includeArg = !string.IsNullOrWhiteSpace(globPattern)
            ? $" --include={SshPath.ShellQuote(Path.GetFileName(globPattern.Trim()))}"
            : "";
        var script = $"sh -c 'grep -rnEI{includeArg} --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=__pycache__ {SshPath.ShellQuote(patternText)} {pathExpr} | head -n {maxResults}'";
        var res = await SshRunner.ExecRawAsync(target.ToProfile(), script, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);

        if (res.ExitCode > 1)
            return ToolResult.Fail(string.IsNullOrWhiteSpace(res.Stderr) ? $"Grep failed for {remotePath}" : res.Stderr.Trim());

        var sb = new StringBuilder();
        var hits = 0;
        foreach (var raw in res.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var formatted = line.StartsWith(target.RemotePath.TrimEnd('/') + "/", StringComparison.Ordinal)
                ? line[(target.RemotePath.TrimEnd('/').Length + 1)..]
                : line;
            if (formatted.Length > 420) formatted = formatted[..420] + "…";
            sb.AppendLine(formatted);
            hits++;
        }

        var output = hits == 0 ? "No matches found." : context.Truncate(sb.ToString(), "matches");
        return ToolResult.Ok(output, $"{hits} matches for /{patternText}/");
    }

    public static async Task<ToolResult> ExecShellAsync(
        SshWorkspaceTarget target, string command, string? workdir, int timeoutSeconds, ToolContext context, CancellationToken ct)
    {
        var remoteDir = SshPath.ResolveRemotePath(target.RemotePath, workdir);
        var dirExpr = RemotePathExpr(remoteDir);
        var wrapped = $"sh -c 'cd {dirExpr} || exit 2; {EscapeSingleQuotes(command)}'";
        var res = await SshRunner.ExecRawAsync(
            target.ToProfile(),
            wrapped,
            timeout: TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)),
            ct: ct).ConfigureAwait(false);

        var combined = string.IsNullOrWhiteSpace(res.Stderr)
            ? res.Stdout
            : string.IsNullOrWhiteSpace(res.Stdout)
                ? res.Stderr
                : $"{res.Stdout}\n{res.Stderr}";
        if (string.IsNullOrWhiteSpace(combined)) combined = "(no output)";

        var isReadOnly = BashTool.IsReadOnlyCommand(command);
        var shortened = command.Length > 60 ? command[..60] + "…" : command;
        var summary = $"{shortened} → exit {res.ExitCode}";
        return res.ExitCode == 0
            ? ToolResult.Ok(context.Truncate(combined, "output"), summary, mutated: isReadOnly ? false : null)
            : new ToolResult
            {
                Success = false,
                Output = context.Truncate($"Exit code {res.ExitCode}\n{combined}", "output"),
                Summary = summary,
                Mutated = isReadOnly ? false : null
            };
    }

    private static string RemotePathExpr(string remotePath)
    {
        if (remotePath == "~") return "\"$HOME\"";
        if (remotePath.StartsWith("~/", StringComparison.Ordinal))
            return "\"$HOME\"/" + SshPath.ShellQuote(remotePath[2..]);
        return SshPath.ShellQuote(remotePath);
    }

    private static string EscapeSingleQuotes(string text) => text.Replace("'", "'\\''", StringComparison.Ordinal);

    private static DecodedTextFile DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DecodedTextFile(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true), bytes[..3]);
        return new DecodedTextFile(Encoding.UTF8.GetString(bytes), new UTF8Encoding(false), []);
    }
}
