using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using Xunit;

namespace SeekClaw.Tests;

public class AgentModeTests
{
    [Theory]
    [InlineData("plan", AgentMode.Plan)]
    [InlineData("readonly", AgentMode.ReadOnly)]
    [InlineData("ro", AgentMode.ReadOnly)]
    [InlineData("auto", AgentMode.Auto)]
    [InlineData("edit", AgentMode.Edit)]
    [InlineData("unknown", AgentMode.Edit)]
    public void Parse_ParsesExpectedModes(string input, AgentMode expected)
    {
        var result = AgentModeExtensions.Parse(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IsReadOnly_IdentifiesReadOnlyModesCorrectly()
    {
        Assert.True(AgentMode.Plan.IsReadOnly());
        Assert.True(AgentMode.ReadOnly.IsReadOnly());
        Assert.False(AgentMode.Edit.IsReadOnly());
        Assert.False(AgentMode.Auto.IsReadOnly());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ToDisplayString_ReturnsFormattedNames()
    {
        Assert.Contains("Plan", AgentMode.Plan.ToDisplayString());
        Assert.Contains("ReadOnly", AgentMode.ReadOnly.ToDisplayString());
        Assert.Contains("Auto", AgentMode.Auto.ToDisplayString());
        Assert.Contains("Edit", AgentMode.Edit.ToDisplayString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FilterToolsByMode_PlanModeFiltersOutMutatingTools()
    {
        var prompts = new FilePromptProvider();
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool(prompts));
        registry.Register(new WriteFileTool(prompts));
        registry.Register(new EditFileTool(prompts));

        var allTools = registry.All;
        var readOnlyTools = allTools.Where(t => !t.Mutating).ToList();

        Assert.Equal(3, allTools.Count);
        Assert.Single(readOnlyTools);
        Assert.Equal("read_file", readOnlyTools[0].Name);
    }
}
