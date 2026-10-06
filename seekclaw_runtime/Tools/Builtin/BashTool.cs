using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

/// <summary>
/// Runs a shell command inside the workspace using an industrial-grade execution engine
/// with head-tail streaming buffers, automatic stdin closure, and Windows JobObject process tree isolation.
/// </summary>
public sealed class BashTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "bash";
    public override string StatusLabel => "Running command";
    public override bool Mutating => true; // A shell command may change anything

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("command", ToolSchema.String("Shell command to execute"), true),
        ("cwd", ToolSchema.String("Working directory (defaults to the workspace root)"), false),
        ("timeout_seconds", ToolSchema.Integer("Timeout in seconds (default from config)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required.");

        var cwd = context.ResolvePath(GetString(arguments, "cwd") ?? ".");
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Working directory not found: {cwd}");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            GetInt(arguments, "timeout_seconds") ?? context.Agent.BashTimeoutSeconds, 1, 3600));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var (shell, shellArgs) = ResolveShell(command);
        var buffer = new HeadTailBuffer(maxBytes: 128 * 1024);
        using var jobScope = new ProcessJobScope();

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in shellArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return ToolResult.Fail($"Failed to launch shell: {shell}");
            }

            // 1. Assign process to Windows JobObject to guarantee killing entire process tree on exit/timeout
            jobScope.AssignProcess(process);

            // 2. Immediately close standard input to prevent blocking on interactive stdin prompts (matching Codex)
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Ignore if stdin already closed
            }

            // 3. Continuously drain stdout and stderr into HeadTailBuffer to prevent pipe deadlocks
            var readStdoutTask = Task.Run(async () =>
            {
                var byteBuffer = new byte[8192];
                var stream = process.StandardOutput.BaseStream;
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(byteBuffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        buffer.PushChunk(byteBuffer, 0, read);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }, CancellationToken.None);

            var readStderrTask = Task.Run(async () =>
            {
                var byteBuffer = new byte[8192];
                var stream = process.StandardError.BaseStream;
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(byteBuffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        buffer.PushChunk(byteBuffer, 0, read);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }, CancellationToken.None);

            // Wait for the process to exit or cancellation/timeout
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(readStdoutTask, readStderrTask).ConfigureAwait(false);

            var outputText = buffer.GetFormattedText(Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                outputText = "(no output)";
            }

            var summary = $"{Shorten(command)} → exit {process.ExitCode}";
            return process.ExitCode == 0
                ? ToolResult.Ok(outputText, summary)
                : new ToolResult
                {
                    Success = false,
                    Output = $"Exit code {process.ExitCode}\n{outputText}",
                    Summary = summary
                };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            return ToolResult.Fail($"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKillProcessTree(process);
            return ToolResult.Fail($"Command execution error: {ex.Message}");
        }
        finally
        {
            if (process != null)
            {
                if (ct.IsCancellationRequested)
                {
                    TryKillProcessTree(process);
                }
                process.Dispose();
            }
        }
    }

    private static void TryKillProcessTree(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may already be dead
        }
    }

    /// <summary>
    /// Resolves the shell executable and arguments for the current OS.
    /// On Windows, prefers pwsh/powershell with UTF-8 prefix and non-interactive flags,
    /// avoiding unintentional invocation of MSYS2/Git bash on Windows command strings.
    /// </summary>
    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            var bashPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            return (bashPath, ["-c", command]);
        }

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null)
        {
            var utf8Script = $"try {{ [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 }} catch {{ }}; {command}";
            return (pwsh, ["-NoProfile", "-NonInteractive", "-NoLogo", "-Command", utf8Script]);
        }

        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);

    private static string Shorten(string command) =>
        command.Length > 60 ? command[..60] + "…" : command;
}
