using System.Text;
using CliWrap;
using CliWrap.Buffered;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Verification;

public sealed record VerifyResult(bool Success, string Command, string Output);

public interface IVerifier
{
    /// <summary>Returns the build/check command for this workspace, or null when none applies.</summary>
    string? ResolveCommand(WorkspaceInfo workspace);

    Task<VerifyResult> VerifyAsync(WorkspaceInfo workspace, CancellationToken ct);
}

/// <summary>
/// Runs the project's build/check command after the agent edits files, so failures
/// feed straight back into the repair loop.
/// </summary>
public sealed class BuildVerifier : IVerifier
{
    public string? ResolveCommand(WorkspaceInfo workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.Config?.VerifyCommand))
            return workspace.Config.VerifyCommand;

        foreach (var kind in workspace.ProjectKinds)
        {
            switch (kind)
            {
                case "dotnet": return "dotnet build --nologo -v q";
                case "rust": return "cargo check --quiet";
                case "go": return "go build ./...";
                case "node":
                    var packageJson = Path.Combine(workspace.Root, "package.json");
                    try
                    {
                        if (File.Exists(packageJson) &&
                            File.ReadAllText(packageJson).Contains("\"build\"", StringComparison.Ordinal))
                            return "npm run build";
                    }
                    catch (IOException) { }
                    break;
            }
        }
        return null;
    }

    public async Task<VerifyResult> VerifyAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        var command = ResolveCommand(workspace);
        if (command is null)
            return new VerifyResult(true, "", "No verification command applies to this workspace.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        var (shell, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/s", "/c", command })
            : ("/bin/bash", ["-c", command]);

        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments(args)
                .WithWorkingDirectory(workspace.Root)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            var output = new StringBuilder(result.StandardOutput);
            if (result.StandardError.Length > 0) output.AppendLine().Append(result.StandardError);

            var text = output.ToString().Trim();
            if (text.Length > 8000) text = text[^8000..]; // errors are usually at the end
            return new VerifyResult(result.ExitCode == 0, command, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VerifyResult(false, command, "Verification timed out after 10 minutes.");
        }
    }
}
