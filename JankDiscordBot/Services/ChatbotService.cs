using System.Text.RegularExpressions;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

/// <summary>
/// Lightweight pattern-matching chatbot for answering questions about Gloomhaven sessions.
/// </summary>
public sealed class ChatbotService
{
    private readonly ScheduleService _schedule;
    private readonly BotRepository _repo;
    private DateTime? _pausedUntilUtc;

    public ChatbotService(ScheduleService schedule, BotRepository repo)
    {
        _schedule = schedule;
        _repo = repo;
    }

    /// <summary>
    /// Pauses chatbot responses for the specified duration.
    /// </summary>
    public void Pause(TimeSpan duration)
    {
        _pausedUntilUtc = DateTime.UtcNow.Add(duration);
    }

    /// <summary>
    /// Resumes chatbot responses immediately.
    /// </summary>
    public void Resume()
    {
        _pausedUntilUtc = null;
    }

    /// <summary>
    /// Checks if the chatbot is currently paused.
    /// </summary>
    public bool IsPaused()
    {
        if (_pausedUntilUtc == null) return false;
        if (DateTime.UtcNow >= _pausedUntilUtc)
        {
            _pausedUntilUtc = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the time remaining until chatbot resumes (null if not paused).
    /// </summary>
    public TimeSpan? GetPauseTimeRemaining()
    {
        if (_pausedUntilUtc == null) return null;
        var remaining = _pausedUntilUtc.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>
    /// Detects if a message is asking about the next session.
    /// Patterns: "are we doing this", "is it happening", "session tonight", "gloomhaven today", etc.
    /// </summary>
    public bool IsAskingAboutNextSession(string message)
    {
        var lower = message.ToLowerInvariant();

        // Remove punctuation for easier matching
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        // Patterns indicating a question about upcoming session
        var patterns = new[]
        {
            @"\b(are|is)\s+(we|it|there|this)\s+(doing|happening|on|still)",
            @"\b(do|does)\s+(we|it)\s+(have|meet)",
            @"\bsession\s+(tonight|today|this\s+week)",
            @"\bgloomhaven\s+(tonight|today|this\s+week)",
            @"\b(tonight|today)\s+.*\s+(on|happening)",
            @"\b(next|upcoming)\s+(session|game|meeting)",
            @"\bwhen.*\b(next|meet|session|gloomhaven)",
            @"\bcancel(l?ed)?",
            @"\b(still|are\s+we)\s+(playing|meeting)"
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a response about the next upcoming session.
    /// </summary>
    public async Task<string?> GetNextSessionResponseAsync()
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled. Try checking your own calendar for once!";

        var dayDiff = DateOnly.FromDateTime(upcoming.EffectiveStartLocal).DayNumber - DateOnly.FromDateTime(nowLocal).DayNumber;
        var timePhrase = dayDiff switch
        {
            0 => "**tonight**",
            1 => "**tomorrow**",
            _ when dayDiff < 7 => $"**this {upcoming.EffectiveStartLocal:dddd}**",
            _ => $"on **{upcoming.EffectiveStartLocal:dddd, MMM d}**"
        };

        if (upcoming.IsCancelled)
        {
            var reason = string.IsNullOrWhiteSpace(upcoming.Note)
                ? ""
                : $"\n**Reason:** {upcoming.Note}";
            return $"🛑 Unfortunately, the session {timePhrase} at **{upcoming.EffectiveStartLocal:h:mm tt}** has been **cancelled**.{reason}";
        }

        // Get rotation info
        var dm = await _repo.GetRotationAsync(RotationRole.DM);
        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        var dmText = dm.Members.Count > 0
            ? $"<@{dm.Members[dm.Index % dm.Members.Count]}>"
            : "_(not set)_";

        var cookText = cook.Members.Count > 0
            ? $"<@{cook.Members[cook.Index % cook.Members.Count]}>"
            : "_(not set)_";

        var moved = upcoming.OriginalDateLocal != DateOnly.FromDateTime(upcoming.EffectiveStartLocal)
            ? " _(moved)_"
            : "";

        var note = string.IsNullOrWhiteSpace(upcoming.Note)
            ? ""
            : $"\n📝 **Note:** {upcoming.Note}";

        return
            $"✅ Yes! Gloomhaven is on {timePhrase} at **{upcoming.EffectiveStartLocal:h:mm tt}**{moved}\n" +
            $"🧙 **DM:** {dmText}\n" +
            $"🍕 **Food:** {cookText}{note}";
    }

    private async Task<SessionInfo?> GetNextSessionAsync(DateOnly start)
    {
        for (int i = 0; i < 366; i++)
        {
            var day = start.AddDays(i);
            var sessions = await _schedule.GetSessionsOccurringOnDateAsync(day);

            foreach (var s in sessions)
            {
                // Skip sessions that already passed today
                if (i == 0)
                {
                    var nowLocal = await _schedule.LocalNowAsync();
                    if (s.EffectiveStartLocal < nowLocal) continue;
                }

                return s;
            }
        }

        return null;
    }
}
