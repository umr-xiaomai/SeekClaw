using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Browser;

/// <summary>Browser actions stay inside the calling conversation's Desktop partition.</summary>
public sealed class BrowserTool(IPromptProvider prompts, IBrowserBridge bridge) : BuiltinTool(prompts)
{
    public override string Name => "browser";
    public override string StatusLabel => "Using conversation browser";
    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => true;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("open | tabs | select | navigate | snapshot | screenshot | click | fill | press | scroll | back | forward | reload | close"), true),
        ("tab_id", ToolSchema.String("Tab ID returned by open/tabs. Omit for the current tab."), false),
        ("url", ToolSchema.String("HTTP(S) URL for open or navigate; localhost development servers are supported."), false),
        ("snapshot_id", ToolSchema.String("Latest snapshot ID, required for element actions."), false),
        ("element_id", ToolSchema.String("Element ID from the latest snapshot for click/fill."), false),
        ("text", ToolSchema.String("Exact text to fill into a field."), false),
        ("key", ToolSchema.String("Enter | Tab | Escape | Backspace | ArrowUp | ArrowDown | ArrowLeft | ArrowRight | Space"), false),
        ("x", ToolSchema.Integer("Viewport CSS x coordinate for screenshot-based click/scroll."), false),
        ("y", ToolSchema.Integer("Viewport CSS y coordinate for screenshot-based click/scroll."), false),
        ("delta_y", ToolSchema.Integer("Vertical scroll distance in CSS pixels, default 600."), false));

    public static string Scope(string workspace, string sessionId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root + "\0" + sessionId)));
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId)) return ToolResult.Fail("The browser requires a conversation.");
        if (!context.NetworkEnabled) return ToolResult.Fail("Network access is disabled for this conversation.");
        var mode = AgentModeExtensions.Parse(context.Workspace.Config?.Mode ?? context.Agent.Mode);
        if (mode.IsReadOnly() && args["action"]?.GetValue<string>() is "click" or "fill" or "press")
            return ToolResult.Fail("Interactive browser actions are disabled in plan/read-only mode.");
        var scope = Scope((context.SessionWorkspace ?? context.Workspace).Root, context.SessionId);
        var result = await bridge.ExecuteAsync(scope, context.SessionId, args, ct).ConfigureAwait(false);
        if (result["error"]?.GetValue<string>() is { } error) return ToolResult.Fail(error);
        IReadOnlyList<ChatImageAttachment>? images = null;
        if (result.Remove("image", out var image) && image is JsonObject data)
        {
            var base64 = data["data"]?.GetValue<string>() ?? "";
            if (base64.Length > 12 * 1024 * 1024) return ToolResult.Fail("Browser screenshot exceeds the size limit.");
            var bytes = Convert.FromBase64String(base64);
            images = [new ChatImageAttachment(Guid.NewGuid().ToString("N"), "browser.png", "image/png", base64, bytes.Length)];
        }
        // Keep snapshot IDs and JSON framing intact when applying the tool output budget.
        var budget = Math.Max(1024, context.MaxOutputChars);
        if (result["text"]?.GetValue<string>() is { } text && text.Length > budget / 2)
            result["text"] = text[..(budget / 2)];
        if (result["elements"] is JsonArray elements)
            while (elements.Count > 0 && result.ToJsonString().Length > budget) elements.RemoveAt(elements.Count - 1);
        return ToolResult.Ok(result.ToJsonString(), images, "Conversation browser", mutated: false);
    }
}
