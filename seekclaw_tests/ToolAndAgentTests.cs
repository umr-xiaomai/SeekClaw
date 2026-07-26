using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Tests;

public sealed class ToolAndAgentTests
{
    [Fact]
    public void EditTool_CountsAndReplacesFirstOccurrence()
    {
        Assert.Equal(2, EditFileTool.CountOccurrences("aXbXc", "X"));
        Assert.Equal(0, EditFileTool.CountOccurrences("abc", "X"));
        Assert.Equal("aYbXc", EditFileTool.ReplaceFirst("aXbXc", "X", "Y"));
    }

    [Fact]
    public void DiffUtil_ProducesUnifiedHunks()
    {
        var diff = DiffUtil.Unified("line1\nline2\nline3\n", "line1\nCHANGED\nline3\n", "test.txt");
        Assert.Contains("--- a/test.txt", diff);
        Assert.Contains("+++ b/test.txt", diff);
        Assert.Contains("-line2", diff);
        Assert.Contains("+CHANGED", diff);
        Assert.Equal("", DiffUtil.Unified("same\n", "same\n", "x"));
    }

    [Fact]
    public void ContextPlanner_KeepsHistoryWithinBudget()
    {
        var model = new ModelConfig { ContextWindow = 4000, MaxOutput = 1000 };
        var big = new string('x', 6000);
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.User($"question {i} {big}"));
            messages.Add(ChatMessage.Assistant($"answer {i} {big}"));
        }

        var fitted = ContextPlanner.FitToWindow(messages, model, "system prompt");

        // Oldest messages drop, but the most recent 6 are always kept (plus the trim notice).
        Assert.True(fitted.Count < messages.Count);
        Assert.True(fitted.Count <= 7);
        // Newest messages survive.
        Assert.Contains(fitted, m => m.Text.StartsWith("answer 9"));
        // A trim notice is inserted at the head.
        Assert.StartsWith("[Earlier conversation history was trimmed", fitted[0].Text);
    }

    [Fact]
    public void ContextPlanner_LeavesShortHistoryUntouched()
    {
        var model = new ModelConfig { ContextWindow = 128_000, MaxOutput = 8_000 };
        var messages = new List<ChatMessage> { ChatMessage.User("hi"), ChatMessage.Assistant("hello") };
        Assert.Same(messages, ContextPlanner.FitToWindow(messages, model, "sys"));
    }

    [Fact]
    public void ContextPlanner_ShrinksOldToolOutputsFirst()
    {
        var model = new ModelConfig { ContextWindow = 12_000, MaxOutput = 2_000 };
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("start"),
            ChatMessage.ToolResult("c1", "read_file", new string('t', 60_000), true),
        };
        for (var i = 0; i < 8; i++) messages.Add(ChatMessage.User($"follow-up {i}"));

        var fitted = ContextPlanner.FitToWindow(messages, model, "sys");
        var tool = fitted.Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("trimmed to fit", tool.Text);
        Assert.True(tool.Text.Length < 1_000);
    }

    [Fact]
    public void ToolOutputBudget_ScalesWithContextWindow_AndRespectsCap()
    {
        var agent = new AgentConfig { MaxToolOutputChars = 60_000 };
        var small = ContextPlanner.ToolOutputBudget(new ModelConfig { ContextWindow = 8_000 }, agent);
        var large = ContextPlanner.ToolOutputBudget(new ModelConfig { ContextWindow = 1_000_000 }, agent);
        Assert.True(small < large);
        Assert.Equal(60_000, large); // capped by config
        Assert.True(small >= 4_000); // floor
    }

    [Fact]
    public void ToolSchema_BuildsValidJsonSchema()
    {
        var schema = ToolSchema.Object(
            ("path", ToolSchema.String("the path"), true),
            ("limit", ToolSchema.Integer("max lines"), false));

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["path"]!["type"]!.GetValue<string>());
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["path"], required);
    }
}
