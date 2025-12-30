namespace GloomhavenRotationBot.Data;

public enum RotationRole
{
    dm = 0,
    cook = 1
}

public sealed class RotationState
{
    public List<ulong> Members { get; set; } = new();
    public int Index { get; set; } = 0;
}

public sealed class MeetingOverride
{
    public DateOnly Date { get; set; }
    public bool IsMeeting { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
