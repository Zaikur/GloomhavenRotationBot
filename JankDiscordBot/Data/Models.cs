namespace GloomhavenRotationBot.Data;

public enum RotationRole
{
    DM = 0,
    Food = 1
}

public sealed class RotationState
{
    public List<ulong> Members { get; set; } = new();
    public int Index { get; set; } = 0;
}

public sealed class MemberProfile
{
    public ulong UserId { get; set; }
    public string? CharacterName { get; set; }
    public string? Notes { get; set; }
    public int? BirthdayMonth { get; set; }
    public int? BirthdayDay { get; set; }
    public int? BirthdayLastSentYear { get; set; }
}

public sealed class MeetingOverride
{
    public DateOnly Date { get; set; }
    public bool IsMeeting { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
