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

        var dmId = dm.GetCurrentAvailableMember();
        var cookId = cook.GetCurrentAvailableMember();

        var dmText = dm.Members.Count == 0
            ? "_(not set)_"
            : dmId is null ? "_(all absent)_" : $"<@{dmId.Value}>";

        var cookText = cook.Members.Count == 0
            ? "_(not set)_"
            : cookId is null ? "_(all absent)_" : $"<@{cookId.Value}>";

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

    /// <summary>
    /// Detects if a message is a greeting to the bot.
    /// Patterns: "hello GLOM", "hi GLOM", "hey bot", etc.
    /// </summary>
    public bool IsGreetingTheBot(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        // Patterns for greetings that mention the bot
        var patterns = new[]
        {
            @"\b(hello|hey|hi|greetings|sup)\b.*\b(bot|gloomhaven|glom)",
            @"\b(bot|gloomhaven|glom)\b.*\b(hello|hey|hi|greetings|sup)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if a message is thanking the bot.
    /// Patterns: "thanks GLOM", "thank you bot", "appreciate it GLOM", etc.
    /// </summary>
    public bool IsThankingTheBot(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        // Patterns for thanks that mention the bot
        var patterns = new[]
        {
            @"\b(thanks|thank\s+you|appreciate|thx)\b.*\b(bot|gloomhaven|glom)",
            @"\b(bot|gloomhaven|glom)\b.*\b(thanks|thank\s+you|appreciate|thx)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a greeting response.
    /// </summary>
    public string GetGreetingResponse()
    {
        var responses = new[]
        {
            "👋 Hello! I'm here to help with Gloomhaven session info. Ask me about upcoming sessions!",
            "Hey there! Need to know about our next Gloomhaven session?",
            "Greetings! Ask me anything about Gloomhaven sessions.",
            "👾 Hi! I'm your Gloomhaven bot. What would you like to know?"
        };
        return responses[new Random().Next(responses.Length)];
    }

    /// <summary>
    /// Gets a thank you response.
    /// </summary>
    public string GetThankYouResponse()
    {
        var responses = new[]
        {
            "You're welcome! Feel free to ask anytime. 🎲",
            "Happy to help! 😊",
            "No problem! Let me know if you need anything else.",
            "Anytime! That's what I'm here for. 👾",
            "Thanks for being so polite! Feel free to ask again."
        };
        return responses[new Random().Next(responses.Length)];
    }

    /// <summary>
    /// Detects if a message is asking about who the DM is.
    /// Patterns: "who is the dm", "who's running it", "who's the dungeon master", etc.
    /// </summary>
    public bool IsAskingAboutDM(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(who|who\s+is)\b.*\b(dm|dungeon\s+master|running|gm|game\s+master)",
            @"\b(dm|dungeon\s+master|gm|game\s+master)\b.*\b(who|is)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if a message is asking if the user themselves is the DM.
    /// Patterns: "am I the dm", "is it my turn to dm", "am I running", etc.
    /// </summary>
    public bool IsAskingIfTheyAreDM(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(am\s+i|is\s+it\s+me)\b.*\b(dm|dungeon\s+master|running|gm|game\s+master)",
            @"\b(is\s+it\s+my\s+turn)\b.*\b(dm|run)",
            @"\bmy\s+turn\b.*\b(dm|run)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if a message is asking about who is making food.
    /// Patterns: "who is making food", "who's cooking", "who brought snacks", etc.
    /// </summary>
    public bool IsAskingAboutFood(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(who|who\s+is)\b.*\b(food|cooking|cook|snacks|bringing|made)",
            @"\b(food|cooking|cook|snacks)\b.*\b(who|is)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if a message is asking if the user themselves is making food.
    /// Patterns: "am I making food", "is it my turn to cook", "am I bringing snacks", etc.
    /// </summary>
    public bool IsAskingIfTheyAreMakingFood(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(am\s+i|is\s+it\s+me)\b.*\b(food|cooking|cook|snacks|bringing)",
            @"\b(is\s+it\s+my\s+turn)\b.*\b(food|cook|bring)",
            @"\bmy\s+turn\b.*\b(food|cook|bring)",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if a message is asking about cancellation status.
    /// Patterns: "is it cancelled", "why is it cancelled", "what's the reason", etc.
    /// </summary>
    public bool IsAskingAboutCancellation(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(is|was)\b.*\b(cancel(l?ed)?|off)",
            @"\b(cancel(l?ed)?)\b",
            @"\bwhy\b.*\b(cancel(l?ed)?|off)",
            @"\breason\b",
            @"\bwhat\s+happened",
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(lower, pattern, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the DM for the next session.
    /// </summary>
    public async Task<string?> GetDMResponseAsync()
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled.";

        var dm = await _repo.GetRotationAsync(RotationRole.DM);

        if (dm.Members.Count == 0)
            return "No DM is currently assigned for the next session.";

        var dmId = dm.GetCurrentAvailableMember();
        if (dmId is null)
            return "Everyone in the DM roster is marked absent for the next session.";

        var dmName = $"<@{dmId.Value}>";
        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        return $"🧙 **{dmName}** will be running the session {timePhrase}.";
    }

    /// <summary>
    /// Checks if the specified user is the DM for the next session.
    /// </summary>
    public async Task<string?> CheckIfUserIsDMAsync(ulong userId)
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled.";

        var dm = await _repo.GetRotationAsync(RotationRole.DM);

        if (dm.Members.Count == 0)
            return "No DM is currently assigned for the next session.";

        var currentDmId = dm.GetCurrentAvailableMember();
        if (currentDmId is null)
            return "Everyone in the DM roster is marked absent for the next session.";

        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        if (currentDmId.Value == userId)
            return $"✅ Yes! You're running the session {timePhrase}. 🧙";

        return $"❌ Nope! <@{currentDmId.Value}> is running the session {timePhrase}.";
    }

    /// <summary>
    /// Gets who is making food for the next session.
    /// </summary>
    public async Task<string?> GetFoodResponseAsync()
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled.";

        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        if (cook.Members.Count == 0)
            return "No one is currently assigned to bring food for the next session.";

        var cookId = cook.GetCurrentAvailableMember();
        if (cookId is null)
            return "Everyone in the food roster is marked absent for the next session.";

        var cookName = $"<@{cookId.Value}>";
        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        return $"🍕 **{cookName}** will bring food for the session {timePhrase}.";
    }

    /// <summary>
    /// Checks if the specified user is making food for the next session.
    /// </summary>
    public async Task<string?> CheckIfUserIsMakingFoodAsync(ulong userId)
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled.";

        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        if (cook.Members.Count == 0)
            return "No one is currently assigned to bring food for the next session.";

        var currentCookId = cook.GetCurrentAvailableMember();
        if (currentCookId is null)
            return "Everyone in the food roster is marked absent for the next session.";

        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        if (currentCookId.Value == userId)
            return $"✅ Yes! You're bringing food for the session {timePhrase}. 🍕";

        return $"❌ Nope! <@{currentCookId.Value}> is bringing food for the session {timePhrase}.";
    }

    /// <summary>
    /// Gets cancellation status and reason (if any) for the next session.
    /// </summary>
    public async Task<string?> GetCancellationStatusResponseAsync()
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var upcoming = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        if (upcoming == null)
            return "I couldn't find any upcoming sessions scheduled.";

        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        if (upcoming.IsCancelled)
        {
            var reason = string.IsNullOrWhiteSpace(upcoming.Note)
                ? "No reason given."
                : $"**Reason:** {upcoming.Note}";
            return $"🛑 The session {timePhrase} at **{upcoming.EffectiveStartLocal:h:mm tt}** is **cancelled**.\n{reason}";
        }

        return $"✅ The session {timePhrase} at **{upcoming.EffectiveStartLocal:h:mm tt}** is **not cancelled** and is going ahead as planned!";
    }

    /// <summary>
    /// Provides a generic response when the chatbot cannot match a request.
    /// </summary>
    public string GetFallbackResponse()
    {
        return "I'm not sure what you mean. Try asking about the next session, who is DM, or who is bringing food.";
    }

    private string FormatSessionTime(SessionInfo upcoming, DateTime nowLocal)
    {
        var dayDiff = DateOnly.FromDateTime(upcoming.EffectiveStartLocal).DayNumber - DateOnly.FromDateTime(nowLocal).DayNumber;
        return dayDiff switch
        {
            0 => "**tonight**",
            1 => "**tomorrow**",
            _ when dayDiff < 7 => $"**this {upcoming.EffectiveStartLocal:dddd}**",
            _ => $"on **{upcoming.EffectiveStartLocal:dddd, MMM d}**"
        };
    }
}
