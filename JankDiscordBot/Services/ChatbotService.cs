using System.Linq;
using System.Text;
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
    private readonly AiTextService _ai;
    private readonly WeatherService _weather;
    private DateTime? _pausedUntilUtc;

    public ChatbotService(ScheduleService schedule, BotRepository repo, AiTextService ai, WeatherService weather)
    {
        _schedule = schedule;
        _repo = repo;
        _ai = ai;
        _weather = weather;
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
    /// Builds a context-rich prompt and delegates to the AI model for responses.
    /// </summary>
    public async Task<string> GenerateResponseAsync(string userMessage, ulong userId, string? username)
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var nextSession = await GetNextSessionAsync(DateOnly.FromDateTime(nowLocal));

        var dmRotation = await _repo.GetRotationAsync(RotationRole.DM);
        var foodRotation = await _repo.GetRotationAsync(RotationRole.Food);

        var profile = await _repo.GetMemberProfileAsync(userId);

        var dmCurrent = dmRotation.Members.Count > 0 ? dmRotation.Members[dmRotation.Index % dmRotation.Members.Count] : (ulong?)null;
        var foodCurrent = foodRotation.Members.Count > 0 ? foodRotation.Members[foodRotation.Index % foodRotation.Members.Count] : (ulong?)null;

        string MentionOrPlaceholder(ulong? id) => id.HasValue ? $"<@{id.Value}>" : "_not set_";

        var sb = new StringBuilder();
        sb.AppendLine("User message:");
        sb.AppendLine(userMessage.Trim());
        sb.AppendLine();

        if (nextSession == null)
        {
            sb.AppendLine("No upcoming sessions were found.");
        }
        else
        {
            sb.AppendLine($"Next session: {nextSession.EffectiveStartLocal:dddd, MMM d @ h:mm tt} local time.");
            if (nextSession.IsCancelled)
                sb.AppendLine("Status: CANCELLED.");
            else
                sb.AppendLine("Status: Scheduled.");

            if (!string.IsNullOrWhiteSpace(nextSession.Note))
                sb.AppendLine($"Session note: {nextSession.Note.Trim()}");

            var wx = await _weather.GetDailyForecastSummaryAsync(DateOnly.FromDateTime(nextSession.EffectiveStartLocal));
            if (!string.IsNullOrWhiteSpace(wx))
                sb.AppendLine(wx);
        }

        sb.AppendLine();
        sb.AppendLine("Rotations:");
        sb.AppendLine($"DM rotation (current first): {BuildRotationLine(dmRotation)}");
        sb.AppendLine($"Food rotation (current first): {BuildRotationLine(foodRotation)}");
        sb.AppendLine($"Current DM: {MentionOrPlaceholder(dmCurrent)}");
        sb.AppendLine($"Current food: {MentionOrPlaceholder(foodCurrent)}");

        if (!string.IsNullOrWhiteSpace(username))
            sb.AppendLine($"Requesting user: {username} ({userId})");
        else
            sb.AppendLine($"Requesting user id: {userId}");

        if (profile != null)
        {
            sb.AppendLine("User profile:");
            if (!string.IsNullOrWhiteSpace(profile.CharacterName))
                sb.AppendLine($"Character: {profile.CharacterName}");
            if (!string.IsNullOrWhiteSpace(profile.Notes))
                sb.AppendLine($"Notes: {profile.Notes}");
            if (profile.BirthdayMonth is not null && profile.BirthdayDay is not null)
                sb.AppendLine($"Birthday: {profile.BirthdayMonth}/{profile.BirthdayDay}");
        }

        var system = "You are a concise, helpful Discord bot for a private Gloomhaven group. Answer using the provided facts. Keep replies under 4 short sentences. Use the provided Discord mention strings exactly as-is. If no session exists, say so. If a session is cancelled, make that clear. If you do not know the answer, say so politely. If the user message is unrelated to Gloomhaven/scheduling, pivot to a witty, sarcastic, slightly cruel jab personalized with any profile details given (character, notes). Never invent members; only use provided info.";

        var fallback = GetFallbackResponse();
        return await _ai.GenerateAsync(system, sb.ToString(), fallback, temperature: 0.35f, maxTokens: 200);
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

        var dmName = $"<@{dm.Members[dm.Index % dm.Members.Count]}>";
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

        var currentDmId = dm.Members[dm.Index % dm.Members.Count];
        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        if (currentDmId == userId)
            return $"✅ Yes! You're running the session {timePhrase}. 🧙";

        return $"❌ Nope! <@{currentDmId}> is running the session {timePhrase}.";
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

        var cookName = $"<@{cook.Members[cook.Index % cook.Members.Count]}>";
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

        var currentCookId = cook.Members[cook.Index % cook.Members.Count];
        var timePhrase = FormatSessionTime(upcoming, nowLocal);

        if (currentCookId == userId)
            return $"✅ Yes! You're bringing food for the session {timePhrase}. 🍕";

        return $"❌ Nope! <@{currentCookId}> is bringing food for the session {timePhrase}.";
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

    private static string BuildRotationLine(RotationState rotation)
    {
        if (rotation.Members.Count == 0) return "_not set_";

        var idx = rotation.Index % rotation.Members.Count;
        var ordered = rotation.Members.Skip(idx).Concat(rotation.Members.Take(idx));
        return string.Join(", ", ordered.Select(id => $"<@{id}>")
            .DefaultIfEmpty("_not set_"));
    }
}
