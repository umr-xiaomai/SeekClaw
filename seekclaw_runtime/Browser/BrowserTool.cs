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
        ("action", ToolSchema.String("open | tabs | select | navigate | snapshot | screenshot | click | right_click | double_click | hover | drag | fill | select_option | press | scroll | evaluate | console | network | request_permission | download | downloads | back | forward | reload | close"), true),
        ("tab_id", ToolSchema.String("Tab ID returned by open/tabs. Omit for the current tab."), false),
        ("url", ToolSchema.String("HTTP(S) URL for open, navigate, or download; localhost development servers are supported."), false),
        ("snapshot_id", ToolSchema.String("Latest snapshot ID, required for element actions."), false),
        ("element_id", ToolSchema.String("Element ID from the latest snapshot for click/right_click/double_click/hover/fill/select_option/drag source."), false),
        ("target_element_id", ToolSchema.String("Target element ID from the latest snapshot for drag drop destination."), false),
        ("text", ToolSchema.String("Exact text to fill into an input/textarea, or option text for select."), false),
        ("value", ToolSchema.String("Option value or visible label for select_option."), false),
        ("key", ToolSchema.String("Key or shortcut combo, e.g. Enter, Tab, Ctrl+A, Ctrl+C, Ctrl+Shift+Z, Alt+ArrowLeft, F5."), false),
        ("modifiers", ToolSchema.Array("Optional modifier keys: Ctrl | Shift | Alt | Meta.", ToolSchema.String("Modifier name.")), false),
        ("button", ToolSchema.String("Mouse button for click: left | right | middle."), false),
        ("click_count", ToolSchema.Integer("Click count (1 or 2)."), false),
        ("x", ToolSchema.Integer("Viewport CSS x coordinate for screenshot-based click/hover/scroll/drag start."), false),
        ("y", ToolSchema.Integer("Viewport CSS y coordinate for screenshot-based click/hover/scroll/drag start."), false),
        ("to_x", ToolSchema.Integer("Destination CSS x coordinate for drag."), false),
        ("to_y", ToolSchema.Integer("Destination CSS y coordinate for drag."), false),
        ("steps", ToolSchema.Integer("Interpolation steps for drag (default 10)."), false),
        ("delta_y", ToolSchema.Integer("Vertical scroll distance in CSS pixels, default 600."), false),
        ("script", ToolSchema.String("JavaScript expression or async function body to execute in the page context for evaluate."), false),
        ("level", ToolSchema.String("Console log filter level: verbose | info | warning | error."), false),
        ("filter", ToolSchema.String("Substring filter for console messages or network URLs."), false),
        ("status_filter", ToolSchema.String("Network status filter: all | error."), false),
        ("clear", ToolSchema.Boolean("Clear console or network logs after reading."), false),
        ("limit", ToolSchema.Integer("Maximum number of console/network entries to return (default 50)."), false),
        ("permission", ToolSchema.String("Permission to request via Desktop approval dialog: camera | microphone | media | geolocation | notifications | clipboard-read."), false),
        ("reason", ToolSchema.String("Human-readable reason shown in the Desktop approval dialog for request_permission or download."), false),
        ("filename", ToolSchema.String("Suggested filename for download."), false));

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
        var actionName = args["action"]?.GetValue<string>();
        if (mode.IsReadOnly() && actionName is "click" or "right_click" or "double_click" or "drag" or "fill" or "select_option" or "press" or "evaluate" or "download")
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
        if (result["value"] is JsonValue val && val.TryGetValue<string>(out var valStr) && valStr.Length > budget / 2)
            result["value"] = valStr[..(budget / 2)];
        if (result["elements"] is JsonArray elements)
            while (elements.Count > 0 && result.ToJsonString().Length > budget) elements.RemoveAt(elements.Count - 1);
        if (result["logs"] is JsonArray logs)
            while (logs.Count > 1 && result.ToJsonString().Length > budget) logs.RemoveAt(0);
        if (result["requests"] is JsonArray requests)
            while (requests.Count > 1 && result.ToJsonString().Length > budget) requests.RemoveAt(0);
        return ToolResult.Ok(result.ToJsonString(), images, "Conversation browser", mutated: false);
    }
}
