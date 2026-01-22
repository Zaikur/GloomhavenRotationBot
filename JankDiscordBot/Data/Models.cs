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
    public const int MaxAiNotesLength = 2000;

    public ulong UserId { get; set; }
    public string? CharacterName { get; set; }
    public string? Notes { get; set; }
    public int? BirthdayMonth { get; set; }
    public int? BirthdayDay { get; set; }
    public int? BirthdayLastSentYear { get; set; }
    
    // Location for personalized weather and future route calculations
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? LocationName { get; set; }
    
    // AI-managed notes about conversations with this user
    public string? AiNotes { get; set; }
}

public sealed class MeetingOverride
{
    public DateOnly Date { get; set; }
    public bool IsMeeting { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public ulong UserId { get; set; }
    public string MessageText { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public bool IsBot { get; set; }
}
