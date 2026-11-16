using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Browser;

/// <summary>Connection-owned transport to Desktop's isolated, visible browser.</summary>
public interface IBrowserBridge
{
    Task<JsonObject> ExecuteAsync(string scope, string sessionId, JsonObject command, CancellationToken ct);
}
