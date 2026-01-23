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

/// <summary>
/// Represents a survey/poll created by a user.
/// </summary>
public sealed class Survey
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ulong CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime CloseAtUtc { get; set; }
    public string Status { get; set; } = "Open"; // Open, Closed, Results Posted
    public ulong? PostChannelId { get; set; }
    public ulong? ResultsMessageId { get; set; }
    public string? HotTakes { get; set; } // AI-generated summary of feedback
    public int InvitedCount { get; set; } // total members invited
    public int RespondedCount { get; set; } // count of members who submitted responses
}

/// <summary>
/// A question within a survey.
/// </summary>
public sealed class SurveyQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SurveyId { get; set; } = string.Empty;
    public int Order { get; set; } // 0-based order
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// An option for a survey question.
/// </summary>
public sealed class SurveyOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string QuestionId { get; set; } = string.Empty;
    public int Order { get; set; } // 0-based order within question
    public string Text { get; set; } = string.Empty;
    public int ResponseCount { get; set; } = 0; // cached count for quick display
}

/// <summary>
/// A user's response to a survey question (one option selected).
/// </summary>
public sealed class SurveyResponse
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SurveyId { get; set; } = string.Empty;
    public ulong UserId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string SelectedOptionId { get; set; } = string.Empty;
    public DateTime SubmittedUtc { get; set; }
}

/// <summary>
/// Anonymous feedback text from a survey respondent.
/// </summary>
public sealed class SurveyFeedback
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SurveyId { get; set; } = string.Empty;
    public ulong UserId { get; set; }
    public string FeedbackText { get; set; } = string.Empty;
    public DateTime SubmittedUtc { get; set; }
}
