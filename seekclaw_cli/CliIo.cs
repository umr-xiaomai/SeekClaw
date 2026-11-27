using System.Text;
using SeekClaw.Cli.Ui;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli;

internal static class CliIo
{
    public static async Task<string> ReadPipedInputAsync(TextReader input, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 4 * 1024 * 1024) throw new ArgumentException("Piped input exceeds 4 MiB of text.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    public static async Task WriteProgressAsync(IEventSubscription subscription, TextWriter error)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var progress = evt switch
            {
                ToolCallStartedEvent tool when !ToolDisplay.IsTerminalInteraction(tool) => $"> {ToolDisplay.Invocation(tool)}",
                StatusEvent status => status.Status,
                WarningEvent warning => warning.Message,
                _ => null,
            };
            if (progress is not null)
                await error.WriteLineAsync(Ansi.StripControls(progress)).ConfigureAwait(false);
        }
    }
}
