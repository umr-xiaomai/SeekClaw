using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Agents;

/// <summary>
/// Messages sent with "steer" while an agent turn is running. The agent drains this
/// queue between model/tool steps so guidance never cancels an in-flight request.
/// </summary>
public sealed class AgentSteeringQueue
{
    private readonly Lock _gate = new();
    private readonly Queue<ChatMessage> _messages = new();
    private bool _accepting = true;

    public bool TryEnqueue(ChatMessage message)
    {
        lock (_gate)
        {
            if (!_accepting) return false;
            _messages.Enqueue(message);
            return true;
        }
    }

    public IReadOnlyList<ChatMessage> Drain()
    {
        lock (_gate)
        {
            var messages = new List<ChatMessage>(_messages.Count);
            while (_messages.Count > 0) messages.Add(_messages.Dequeue());
            return messages;
        }
    }

    /// <summary>
    /// Atomically closes an empty queue. When a message raced with completion the queue
    /// stays open so the Agent can run one more step and drain it.
    /// </summary>
    public bool TryCompleteIfEmpty()
    {
        lock (_gate)
        {
            if (_messages.Count > 0) return false;
            _accepting = false;
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate) _accepting = false;
    }
}
