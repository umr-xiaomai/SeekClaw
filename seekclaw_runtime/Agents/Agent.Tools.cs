using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed partial class Agent
{
    private readonly record struct ToolExecution(ChatMessage Message, bool ToolMutated);

    private async Task<ToolExecution> ExecuteToolAsync(
        ToolCallRequest call,
        WorkspaceInfo workspace,
        ModelInfo model,
        string sessionId,
        string? turnRequestId,
        CancellationToken ct)
    {
        var tool = toolRegistry.Resolve(call.Name);
        var argsSummary = SummarizeArguments(call.ArgumentsJson);
        events.Publish(new ToolCallStartedEvent(call.Id, call.Name, argsSummary));

        if (tool is null)
        {
            var message = $"Unknown tool: {call.Name}";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);
        if (mode.IsReadOnly() && tool.Mutating)
        {
            var message = $"Tool execution denied: '{tool.Name}' is not allowed in {mode.ToDisplayString()}. Switch mode via '/mode edit' or '/mode auto' to allow file mutations.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        events.Publish(new StatusEvent(tool.StatusLabel, argsSummary));

        // Best-effort repair of truncated / trailing-garbage arguments, then execute with
        // the recovered object. Unrecoverable arguments become an explicit tool error so the
        // model regenerates the call instead of the provider rejecting the whole request.
        var parsedArguments = ToolArguments.Parse(call.ArgumentsJson);
        if (parsedArguments.Obj is null)
        {
            var message = "Invalid tool arguments: the model produced arguments that are not valid JSON. Regenerate the tool call with valid JSON arguments.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }
        var arguments = parsedArguments.Obj;

        if (RequiresApproval(mode, tool))
        {
            ToolApprovalDecision decision;
            try
            {
                decision = await approvalGate.RequestAsync(new ToolApprovalRequest(
                    sessionId,
                    turnRequestId,
                    call.Id,
                    tool.Name,
                    call.ArgumentsJson,
                    argsSummary,
                    mode,
                    tool.Mutating,
                    tool.RequiresNetwork,
                    tool.Name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                decision = ToolApprovalDecision.Deny($"Approval channel failed: {ex.Message}");
            }

            if (!decision.Approved)
            {
                var message = $"Tool execution denied: {decision.Reason ?? "the user rejected this action."}";
                events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
                return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
            }
        }

        var context = new ToolContext
        {
            Workspace = workspace,
            Events = events,
            Agent = configStore.Config.Agent,
            MaxOutputChars = ContextPlanner.ToolOutputBudget(model.Model, configStore.Config.Agent),
            CallId = call.Id,
            Coordinator = fileLocks,
            Owner = lockScope.Owner,
            SubAgentDepth = subAgentScope?.Depth ?? 0,
            ApprovalGate = approvalGate,
        };

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{tool.Name} crashed: {ex.Message}");
        }
        stopwatch.Stop();

        events.Publish(new ToolCallCompletedEvent(
            call.Id, call.Name, result.Success,
            result.Summary ?? Firstline(result.Output), stopwatch.Elapsed));

        var filePath = result.FilePath is null
            ? null
            : Path.IsPathRooted(result.FilePath)
                ? Path.GetRelativePath(workspace.Root, result.FilePath)
                : result.FilePath;
        return new ToolExecution(
            ChatMessage.ToolResult(call.Id, call.Name, result.Output, result.Success, result.Diff, filePath, result.Images),
            (result.Mutated ?? tool.Mutating) && result.Success);
    }

    private static bool RequiresApproval(AgentMode mode, ITool tool)
    {
        if (mode is not (AgentMode.Manual or AgentMode.Guardrail)) return false;

        if (tool.Mutating
            || tool.RequiresNetwork
            || tool.Name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // These tools can trigger consequential external actions even though their direct
        // file mutation flag is false: sub-agents may mutate with the parent's authority,
        // and screen capture exposes desktop content.
        return tool.Name.Equals("invoke_subagent", StringComparison.OrdinalIgnoreCase)
               || (mode == AgentMode.Manual
                   && tool.Name.Equals("capture_screen", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- misc

    public static string SummarizeArguments(string argumentsJson)
    {
        try
        {
            if (JsonNode.Parse(argumentsJson) is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("command", out var cmd) && cmd is not null)
                    return cmd.ToString().Trim().ReplaceLineEndings(" ");
                if (obj.TryGetPropertyValue("cmd", out var c) && c is not null)
                    return c.ToString().Trim().ReplaceLineEndings(" ");
                if (obj.TryGetPropertyValue("path", out var p) && p is not null)
                    return p.ToString();
                if (obj.TryGetPropertyValue("file_path", out var fp) && fp is not null)
                    return fp.ToString();
                if (obj.TryGetPropertyValue("pattern", out var pat) && pat is not null)
                    return pat.ToString();
                if (obj.TryGetPropertyValue("query", out var q) && q is not null)
                    return q.ToString();

                var parts = obj
                    .Where(kv => kv.Value is JsonValue)
                    .Take(3)
                    .Select(kv =>
                    {
                        var value = kv.Value!.ToString();
                        if (value.Length > 60) value = value[..60] + "…";
                        return $"{kv.Key}: {value.ReplaceLineEndings(" ")}";
                    });
                return string.Join(", ", parts);
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static string Firstline(string text)
    {
        var line = text.AsSpan().TrimStart();
        var newline = line.IndexOf('\n');
        var result = newline >= 0 ? line[..newline] : line;
        return result.Length > 160 ? string.Concat(result[..160], "…") : result.ToString();
    }
}
