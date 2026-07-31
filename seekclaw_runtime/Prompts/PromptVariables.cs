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
        var hasWorkspace = workspace is { IsGlobal: false };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cwd"] = hasWorkspace ? workspace!.Root : "",
            ["datetime"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["os"] = RuntimeInformation.OSDescription,
            ["platform"] = RuntimeInformation.RuntimeIdentifier,
            ["workspace"] = hasWorkspace ? workspace!.Root : "",
            ["project"] = hasWorkspace ? workspace!.Name : "",
            ["language"] = hasWorkspace ? string.Join(", ", workspace!.ProjectKinds) : "",
            ["scope"] = workspace?.IsGlobal == true ? "global" : "workspace",
            ["model"] = model?.Model.Id ?? "",
            ["provider"] = model?.Provider.Id ?? "",
            ["vision"] = model?.Model.Capabilities.Vision == true ? "true" : "false",
            ["image"] = model?.Model.Capabilities.Image == true ? "true" : "false",
            ["tool"] = toolNames is null ? "" : string.Join(", ", toolNames),
            ["memory"] = memory ?? "",
        };
        return variables;
    }

    /// <summary>
    /// Gives a vision-capable model an explicit capability contract. This is appended by the
    /// runtime even when a workspace overrides the normal system prompt, so a model cannot
    /// mistake a UI capability declaration for an unsupported text-only session.
    /// </summary>
    public static string BuildCapabilityInstruction(bool vision, bool imageOutput = false)
    {
        if (!vision) return "";

        var outputInstruction = imageOutput
            ? "The configured provider may also expose image output; use it only when the request and provider protocol support it."
            : "The current response channel is text unless the provider explicitly exposes another output modality; do not promise image generation.";

        return $"""
[MODEL CAPABILITIES: MULTIMODAL]
This model is configured to accept visual image input. Image attachments in user messages are available to you as part of the request; inspect and reason about their visual content together with the text.
Do not say that you lack a vision encoder, cannot see images, or are text-only when an image is attached. Refer to the relevant image or filename when useful, and be explicit about uncertainty when visual details are ambiguous.
{outputInstruction}
""";
    }
}
