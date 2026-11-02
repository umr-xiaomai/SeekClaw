using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Presentation;

/// <summary>Presents an existing workspace file; never executes or opens it.</summary>
public sealed class PresentArtifactTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "present_artifact";
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("Existing workspace file to show as a card; .html/.htm supports sandboxed preview"), true),
        ("title", ToolSchema.String("Short display title"), false),
        ("description", ToolSchema.String("One sentence explaining the deliverable"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = context.ResolvePath(GetString(args, "path") ?? "");
        if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail("Artifact file not found."));
        var widget = ArtifactWidget.FromFile(path, GetString(args, "title"), GetString(args, "description"),
            context.Workspace.WorkingDirectory);
        return Task.FromResult(ToolResult.Ok($"Presented {Path.GetRelativePath(context.Workspace.Root, path)}", widget.Title)
            with { Widgets = [widget] });
    }
}
