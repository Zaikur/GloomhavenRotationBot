namespace GloomhavenRotationBot.Services;

public sealed class BotStatusService
{
    private readonly object _lock = new();

    public string State { get; private set; } = "Starting";
    public string? Details { get; private set; }
    public DateTimeOffset LastChangeUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void Set(string state, string? details = null)
    {
        lock (_lock)
        {
            State = state;
            Details = details;
            LastChangeUtc = DateTimeOffset.UtcNow;
        }
    }
}
