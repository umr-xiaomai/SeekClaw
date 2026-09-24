using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.ComputerUse;

/// <summary>
/// Tool for structural inspection of the desktop and active application UI hierarchy.
/// Gives the AI agent exact control names, IDs, bounding boxes, and center coordinates.
/// </summary>
public sealed class ComputerInspectTool(
    IPromptProvider prompts,
    IComputerDriver driver) : BuiltinTool(prompts)
{
    public override string Name => "computer_inspect";
    public override string StatusLabel => "Inspecting desktop UI";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("take_screenshot", ToolSchema.Boolean("Whether to also take and attach a screenshot along with UI elements"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var takeScreenshot = GetBool(arguments, "take_screenshot");
        var hierarchy = await driver.GetVisualElementsAsync(ct).ConfigureAwait(false);

        ScreenCapture? capture = null;
        if (takeScreenshot)
        {
            capture = await driver.CaptureScreenAsync(0, ct).ConfigureAwait(false);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[Active Window]: {hierarchy.ActiveWindowTitle ?? "(None/Desktop)"}");
        if (hierarchy.Success)
        {
            sb.AppendLine($"[Interactive Elements Found]: {hierarchy.Elements.Count}");
            foreach (var el in hierarchy.Elements.Take(40))
            {
                var label = string.IsNullOrWhiteSpace(el.Name) ? "(unnamed)" : el.Name;
                sb.AppendLine($"  - [{el.ControlType}] \"{label}\" Center:({el.CenterX}, {el.CenterY}) Bounds:[{el.X}, {el.Y}, {el.Width}x{el.Height}] Id:{el.Id}");
                if (el.Children is { Count: > 0 })
                {
                    foreach (var child in el.Children.Take(20))
                    {
                        var childLabel = string.IsNullOrWhiteSpace(child.Name) ? "(unnamed)" : child.Name;
                        sb.AppendLine($"      * [{child.ControlType}] \"{childLabel}\" Center:({child.CenterX}, {child.CenterY}) Bounds:[{child.X}, {child.Y}, {child.Width}x{child.Height}] Id:{child.Id}");
                    }
                }
            }
        }
        else
        {
            sb.AppendLine($"[UI Inspection Warning]: {hierarchy.Error ?? "Could not retrieve control hierarchy, falling back to visual coordinates."}");
        }

        List<ChatImageAttachment>? attachments = null;
        string? base64 = null;
        if (capture is not null)
        {
            base64 = capture.ToBase64();
            attachments =
            [
                new ChatImageAttachment(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Name: "computer_inspect.png",
                    MediaType: capture.Format,
                    Data: base64,
                    SizeBytes: capture.Bytes.Length)
            ];
            sb.AppendLine("\n[Screenshot attached for visual confirmation]");
        }

        context.Events.Publish(new ComputerActionEvent(
            Action: "inspect",
            X: null,
            Y: null,
            Target: hierarchy.ActiveWindowTitle,
            Success: hierarchy.Success,
            Error: hierarchy.Error,
            Base64Screenshot: base64));

        return ToolResult.Ok(sb.ToString(), attachments, $"Inspected UI: {hierarchy.ActiveWindowTitle ?? "Desktop"}");
    }
}


/// <summary>
/// Unified computer operation tool supporting clicks, cursor movement, typing, key combinations,
/// scrolling, and screen capture. Compatible with standard AI computer use conventions.
/// </summary>
public sealed class ComputerTool(
    IPromptProvider prompts,
    IComputerDriver driver,
    ComputerUseConfig config) : BuiltinTool(prompts)
{
    public override string Name => "computer";
    public override string StatusLabel => "Operating computer";
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("Action: 'screenshot', 'left_click', 'right_click', 'middle_click', 'double_click', 'mouse_move', 'type', 'key', 'scroll', 'focus_window', 'list_windows'"), true),
        ("coordinate", ToolSchema.Array("Coordinate pair [x, y] to click or move to", ToolSchema.Integer("pixel value")), false),
        ("x", ToolSchema.Integer("X coordinate (alternative to coordinate array)"), false),
        ("y", ToolSchema.Integer("Y coordinate (alternative to coordinate array)"), false),
        ("text", ToolSchema.String("Text to type, key combo to press, or window title/id to focus"), false),
        ("delta_x", ToolSchema.Integer("Horizontal scroll delta"), false),
        ("delta_y", ToolSchema.Integer("Vertical scroll delta"), false),
        ("auto_screenshot", ToolSchema.Boolean("Whether to capture and return screenshot after the action (default false)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var rawAction = GetString(arguments, "action")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            return ToolResult.Fail("The 'action' parameter is required.");
        }

        var (x, y) = ResolveCoordinates(arguments);
        var text = GetString(arguments, "text") ?? "";
        var deltaX = ReadIntNode(arguments["delta_x"]) ?? 0;
        var deltaY = ReadIntNode(arguments["delta_y"]) ?? 0;
        var autoScreenshot = arguments.ContainsKey("auto_screenshot") && GetBool(arguments, "auto_screenshot");

        ActionResult result;
        switch (rawAction)
        {
            case "screenshot":
                autoScreenshot = true;
                result = ActionResult.Ok("Screenshot captured", "screenshot");
                break;

            case "left_click" or "click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Click action requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Left, 1, ct).ConfigureAwait(false);
                break;

            case "right_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Right click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Right, 1, ct).ConfigureAwait(false);
                break;

            case "middle_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Middle click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Middle, 1, ct).ConfigureAwait(false);
                break;

            case "double_click":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Double click requires coordinates [x, y].");
                result = await driver.ClickAsync(x.Value, y.Value, MouseButton.Left, 2, ct).ConfigureAwait(false);
                break;

            case "mouse_move" or "move":
                if (!x.HasValue || !y.HasValue) return ToolResult.Fail("Mouse move requires coordinates [x, y].");
                result = await driver.MoveMouseAsync(x.Value, y.Value, ct).ConfigureAwait(false);
                break;

            case "type":
                if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Type action requires non-empty 'text'.");
                result = await driver.TypeTextAsync(text, ct).ConfigureAwait(false);
                break;

            case "key":
                if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Key action requires non-empty 'text' specifying key combo.");
                result = await driver.SendKeyAsync(text, ct).ConfigureAwait(false);
                break;

            case "scroll":
                result = await driver.ScrollAsync(x ?? 0, y ?? 0, deltaX, deltaY, ct).ConfigureAwait(false);
                break;

            case "focus_window":
                if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("Focus window action requires non-empty 'text' specifying window title or ID.");
                var focused = await driver.WindowManager.FocusWindowAsync(text, ct).ConfigureAwait(false);
                result = focused 
                    ? ActionResult.Ok($"Focused window matching '{text}'", "focus_window", target: text)
                    : ActionResult.Failed($"Could not find or focus window matching '{text}'", "focus_window");
                break;

            case "list_windows":
                var windows = await driver.WindowManager.ListWindowsAsync(ct).ConfigureAwait(false);
                var winSb = new StringBuilder();
                winSb.AppendLine($"Found {windows.Count} open windows:");
                foreach (var w in windows)
                {
                    winSb.AppendLine($"  - [{(w.IsActive ? "ACTIVE" : "WINDOW")}] \"{w.Title}\" ({w.Width}x{w.Height} at {w.X},{w.Y}) Id:{w.Id}");
                }
                result = ActionResult.Ok(winSb.ToString().TrimEnd(), "list_windows");
                break;

            default:
                return ToolResult.Fail($"Unsupported action '{rawAction}'. Supported: screenshot, left_click, right_click, middle_click, double_click, mouse_move, type, key, scroll, focus_window, list_windows.");
        }

        if (config.ActionDelayMs > 0 && rawAction != "screenshot")
        {
            await Task.Delay(config.ActionDelayMs, ct).ConfigureAwait(false);
        }

        ScreenCapture? postCapture = null;
        if (autoScreenshot)
        {
            postCapture = await driver.CaptureScreenAsync(0, ct).ConfigureAwait(false);
        }

        List<ChatImageAttachment>? images = null;
        string? base64 = null;
        if (postCapture is not null)
        {
            base64 = postCapture.ToBase64();
            images =
            [
                new ChatImageAttachment(
                    Id: Guid.NewGuid().ToString("N")[..8],
                    Name: $"computer_{rawAction}.png",
                    MediaType: postCapture.Format,
                    Data: base64,
                    SizeBytes: postCapture.Bytes.Length)
            ];
        }

        // Publish event for Desktop Live Panel and Daemon subscribers
        context.Events.Publish(new ComputerActionEvent(
            Action: rawAction,
            X: x,
            Y: y,
            Target: !string.IsNullOrEmpty(text) ? text : null,
            Success: result.Success,
            Error: result.Success ? null : result.Message,
            Base64Screenshot: base64));

        if (!result.Success)
        {
            return new ToolResult
            {
                Success = false,
                Output = $"Computer action '{rawAction}' failed: {result.Message}",
                Summary = $"Computer {rawAction} failed",
                Images = images
            };
        }

        var message = result.Message ?? $"Executed {rawAction}";
        if (images is not null)
        {
            message += "\n(Latest screen state captured and attached)";
        }

        return ToolResult.Ok(message, images, $"Computer: {rawAction}");
    }

    private static (int? x, int? y) ResolveCoordinates(JsonObject args)
    {
        if (args["coordinate"] is JsonArray arr && arr.Count >= 2)
        {
            var cx = ReadIntNode(arr[0]);
            var cy = ReadIntNode(arr[1]);
            if (cx.HasValue && cy.HasValue) return (cx, cy);
        }

        var x = ReadIntNode(args["x"]);
        var y = ReadIntNode(args["y"]);
        return (x, y);
    }

    private static int? ReadIntNode(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<double>(out var d)) return (int)d;
                if (val.TryGetValue<long>(out var l)) return (int)l;
                if (int.TryParse(val.ToString(), out var parsed)) return parsed;
            }
        }
        catch { }
        return null;
    }
}

