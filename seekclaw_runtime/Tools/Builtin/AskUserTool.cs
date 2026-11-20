using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

public sealed class AskUserTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public const string ToolName = "ask_user";
    public override string Name => ToolName;
    public override string StatusLabel => "Waiting for user input";
    public override bool RequiresWorkspace => false;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("question", ToolSchema.String("One focused question, in the user's language"), true),
        ("options", ToolSchema.Array("Two to five distinct choices", ToolSchema.String("Choice label")), true));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (context.AskUser is not { } askUser)
            return ToolResult.Fail("Interactive questions are unavailable. Ask in your chat reply instead.");

        var question = ReadText(arguments["question"]);
        if (question.Length is 0 or > 1000 || arguments["options"] is not JsonArray choices || choices.Count is < 2 or > 5)
            return ToolResult.Fail("Supply one nonempty question (up to 1000 characters) and two to five choices.");

        var options = choices.Select(ReadText).ToArray();
        if (options.Any(option => option.Length is 0 or > 200)
            || options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Length)
            return ToolResult.Fail("Choices must be distinct, nonempty strings of at most 200 characters.");

        var answer = await askUser(question, options, ct).ConfigureAwait(false);
        return ToolResult.Ok(new JsonObject { ["answer"] = answer }.ToJsonString(), "User answered", mutated: false);
    }

    private static string ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
}
