using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Execution;

namespace SeekClaw.Tests;

public sealed class CommandSessionTransportTests
{
    private const string Owner = "transport-test";
    private static readonly string WorkingDirectory = Directory.GetCurrentDirectory();

    [Fact]
    public async Task OutputBudget_ContinuesFromTheDeliveredCursorWithoutRewinding()
    {
        await using var manager = new CommandSessionManager();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var text = new string('é', 1024) + "-end-of-output";
        var command = OperatingSystem.IsWindows()
            ? "[Console]::Write(([string][char]233 * 1024) + '-end-of-output')"
            : "i=0; while [ \"$i\" -lt 1024 ]; do printf '\\303\\251'; i=$((i+1)); done; printf '%s' '-end-of-output'";
        const int budget = 1800;
        var first = await manager.ExecAsync(Owner, command, WorkingDirectory, 10, tty: false, 10_000,
            deadline.Token, maxOutputChars: budget);
        var id = first["sessionId"]!.GetValue<string>();
        Assert.True(first["hasMore"]!.GetValue<bool>());
        Assert.NotEmpty(Output(first));
        Assert.True(first.ToJsonString().Length <= budget);
        Assert.Equal(Output(first).Length, first["cursor"]!.GetValue<long>());
        Assert.Null(first["command"]);

        var raw = await manager.ReadAsync(Owner, id, ct: deadline.Token);
        Assert.Equal(text, Output(raw));
        Assert.Equal(command, raw["command"]!.GetValue<string>());
        var second = await manager.ReadAsync(Owner, id, ct: deadline.Token, maxOutputChars: budget);
        var replay = await manager.ReadAsync(Owner, id, 0, ct: deadline.Token, maxOutputChars: budget);
        Assert.Equal(Output(first), Output(replay));

        var transcript = new StringBuilder(Output(first)).Append(Output(second));
        var page = second;
        while (page["hasMore"]!.GetValue<bool>())
        {
            page = await manager.ReadAsync(Owner, id, ct: deadline.Token, maxOutputChars: budget);
            Assert.NotEmpty(Output(page));
            Assert.True(page.ToJsonString().Length <= budget);
            transcript.Append(Output(page));
        }
        Assert.Equal(text, transcript.ToString());
    }

    [Fact]
    public void TerminalTranscript_CollapsesSplitRedrawsAndEmitsAPromptOnce()
    {
        var buffer = new CommandOutputBuffer();
        var transcript = new TerminalTranscript(buffer);
        transcript.Append("10%\r\u001b[2");
        transcript.Append("K90%\r\n\u001b]0;window");
        transcript.Append(" title\u001b\\\u001b[32mPass");
        transcript.Append("word: \u001b[0m");
        Assert.True(transcript.HasPromptUpdate);
        transcript.PublishPrompt();
        Assert.False(transcript.HasPromptUpdate);
        Assert.Equal("90%\nPassword: ", buffer.Slice(0, buffer.End));
        transcript.PublishPrompt();
        transcript.Append("\r\nconnected\r\n");
        Assert.Equal("90%\nPassword: \nconnected\n", buffer.Slice(0, buffer.End));
    }

    [Fact]
    public void TerminalTranscript_AbsoluteCursorMoveKeepsRowsSeparate()
    {
        var buffer = new CommandOutputBuffer();
        var transcript = new TerminalTranscript(buffer);
        transcript.Append("header-v1\r\nstatus-before");
        transcript.PublishPrompt();
        var cursor = buffer.End;
        transcript.Append("\u001b[H");
        Assert.Equal(cursor, buffer.End);
        Assert.False(transcript.HasPromptUpdate);
        transcript.Append("header-v2\r\nstatus-after!\r\nEnter ");
        transcript.PublishPrompt();
        transcript.Append("value: ");
        transcript.PublishPrompt();
        Assert.Equal("header-v1\nstatus-before\nheader-v2\nstatus-after!\nEnter value: ", buffer.Slice(0, buffer.End));
    }

    [Theory]
    [InlineData("\u001b[A\r")]
    [InlineData("\u001b[B\r")]
    [InlineData("\u001b[E")]
    [InlineData("\u001b[F")]
    [InlineData("\u001b[1d\r")]
    public void TerminalTranscript_VerticalCursorMovesDoNotOverwriteUnpublishedRows(string move)
    {
        var buffer = new CommandOutputBuffer();
        var transcript = new TerminalTranscript(buffer);
        transcript.Append("header\r\nstatus-before");
        transcript.Append(move);
        transcript.Append("new row\r\n");

        Assert.Equal("header\nstatus-before\nnew row\n", buffer.Slice(0, buffer.End));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsPseudoTerminal_PreservesLinesLongerThanTheViewport(bool newline)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var manager = new CommandSessionManager();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var method = newline ? "WriteLine" : "Write";
        var command = $"[Console]::{method}('first/' + ('0123456789' * 45) + '/中文/last'); exit 7";
        var expected = "first/" + string.Concat(Enumerable.Repeat("0123456789", 45)) + "/中文/last";
        var result = await manager.ExecAsync(Owner, command, WorkingDirectory, 12, tty: true, 12_000,
            deadline.Token, maxOutputChars: 4000);
        Assert.Equal("exited", result["status"]!.GetValue<string>());
        Assert.Equal(7, result["exitCode"]!.GetValue<int>());
        Assert.Contains(expected, Output(result));
        Assert.DoesNotContain('\u001b', Output(result));
        var raw = await manager.ReadAsync(Owner, result["sessionId"]!.GetValue<string>(), ct: deadline.Token);
        Assert.Contains("first/", Output(raw));
        Assert.Contains("/中文/last", Output(raw));
        Assert.Contains('\u001b', Output(raw));
    }

    [Fact]
    public async Task LifetimeExpiration_IsDistinguishedFromTermination()
    {
        await using var manager = new CommandSessionManager();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var result = await manager.ExecAsync(Owner, command, WorkingDirectory, 1, tty: false, 15_000, deadline.Token);
        Assert.Equal("timed_out", result["status"]!.GetValue<string>());
        Assert.Null(result["exitCode"]);
    }

    private static string Output(JsonObject result) => result["output"]!.GetValue<string>();
}
