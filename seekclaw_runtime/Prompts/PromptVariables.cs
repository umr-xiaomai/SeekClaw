using System.Runtime.InteropServices;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Prompts;

/// <summary>Builds the standard {{variable}} set available to every prompt file.</summary>
public static class PromptVariables
{
    public static Dictionary<string, string> Build(
        WorkspaceInfo? workspace,
        ModelInfo? model,
        IReadOnlyCollection<string>? toolNames = null,
        string? memory = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cwd"] = Directory.GetCurrentDirectory(),
            ["datetime"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["os"] = RuntimeInformation.OSDescription,
            ["platform"] = RuntimeInformation.RuntimeIdentifier,
            ["workspace"] = workspace?.Root ?? Directory.GetCurrentDirectory(),
            ["project"] = workspace?.Name ?? Path.GetFileName(Directory.GetCurrentDirectory()),
            ["language"] = workspace is null ? "" : string.Join(", ", workspace.ProjectKinds),
            ["model"] = model?.Model.Id ?? "",
            ["provider"] = model?.Provider.Id ?? "",
            ["tool"] = toolNames is null ? "" : string.Join(", ", toolNames),
            ["memory"] = memory ?? "",
        };
        return variables;
    }
}
