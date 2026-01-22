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
    private readonly AppSettingsService _settings;
    private DateTime? _pausedUntilUtc;

    public ChatbotService(ScheduleService schedule, BotRepository repo, AiTextService ai, WeatherService weather, AppSettingsService settings)
    {
        _schedule = schedule;
        _repo = repo;
        _ai = ai;
        _weather = weather;
        _settings = settings;
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

        var wantsWeather = IsWeatherQuery(userMessage);

        var dmRotation = await _repo.GetRotationAsync(RotationRole.DM);
        var foodRotation = await _repo.GetRotationAsync(RotationRole.Food);

        var profile = await _repo.GetMemberProfileAsync(userId);

        var dmCurrent = dmRotation.Members.Count > 0 ? dmRotation.Members[dmRotation.Index % dmRotation.Members.Count] : (ulong?)null;
        var foodCurrent = foodRotation.Members.Count > 0 ? foodRotation.Members[foodRotation.Index % foodRotation.Members.Count] : (ulong?)null;

        string MentionOrPlaceholder(ulong? id) => id.HasValue ? $"<@{id.Value}>" : "_not set_";

        var sb = new StringBuilder();
        
        // Message history for context
        var recentMessages = await _repo.GetRecentChatMessagesAsync(userId, conversationWindowMinutes: 30, maxMessages: 20);
        if (recentMessages.Count > 0)
        {
            sb.AppendLine("Recent conversation history:");
            foreach (var msg in recentMessages)
            {
                var sender = msg.IsBot ? "Bot" : (username ?? "User");
                var timeAgo = (DateTime.UtcNow - msg.TimestampUtc).TotalMinutes;
                var timeLabel = timeAgo < 1 ? "just now" : timeAgo < 60 ? $"{(int)timeAgo}m ago" : $"{(int)(timeAgo / 60)}h ago";
                sb.AppendLine($"[{timeLabel}] {sender}: {msg.MessageText}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Current user message:");
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
                sb.AppendLine("Status: Cancelled.");
            else
                sb.AppendLine("Status: Scheduled.");

            if (!string.IsNullOrWhiteSpace(nextSession.Note))
                sb.AppendLine($"Session note: {nextSession.Note.Trim()}");

            if (wantsWeather)
            {
                // Try to get per-user weather first, fall back to global weather
                string? wx = null;
                if (profile?.Latitude != null && profile?.Longitude != null)
                {
                    var (_, _, units) = await _settings.GetWeatherConfigAsync();
                    wx = await _weather.GetDailyForecastSummaryForLocationAsync(
                        DateOnly.FromDateTime(nextSession.EffectiveStartLocal),
                        profile.Latitude.Value,
                        profile.Longitude.Value,
                        units);
                    if (!string.IsNullOrWhiteSpace(wx) && !string.IsNullOrWhiteSpace(profile.LocationName))
                        wx = $"{wx} (at {profile.LocationName})";
                }
                else
                {
                    wx = await _weather.GetDailyForecastSummaryAsync(DateOnly.FromDateTime(nextSession.EffectiveStartLocal));
                }

                if (!string.IsNullOrWhiteSpace(wx))
                    sb.AppendLine(wx);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Rotations:");
        sb.AppendLine($"DM rotation (current first): {BuildRotationLine(dmRotation)}");
        sb.AppendLine($"Food rotation (current first): {BuildRotationLine(foodRotation)}");
        sb.AppendLine($"Current DM: {MentionOrPlaceholder(dmCurrent)}");
        sb.AppendLine($"Current food: {MentionOrPlaceholder(foodCurrent)}");

        sb.AppendLine();
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
            if (profile.Latitude != null && profile.Longitude != null && !string.IsNullOrWhiteSpace(profile.LocationName))
                sb.AppendLine($"Location: {profile.LocationName} ({profile.Latitude:F4}, {profile.Longitude:F4})");

            // Include AI notes
            if (!string.IsNullOrWhiteSpace(profile.AiNotes))
            {
                sb.AppendLine("AI notes about this user:");
                sb.AppendLine(profile.AiNotes);
            }
        }

        var system = $@"You are a Discord bot for a private Gloomhaven group. Your role is a tired, witty DM who may be fed up with the players shenanigans. Answer using the provided facts, chat history, and notes provided. Keep replies under 4 short sentences. Use the provided Discord mention strings exactly as-is. If no session exists, say so. If a session is cancelled, make that clear. If you do not know the answer, say so. If the user message to you seems ridiculous, they ask the same question repeatedly, or you just feel like it, pivot to a witty, sarcastic, slightly cruel jab (e.g. -Forgot again? How typical of vermi- I mean a Vermling.-, -Rain today, at least that'll help clean your next massacre-) personalized with any profile details given (character, notes). Never invent members; only use provided info and keep responses on theme..

AI NOTES FEATURE: You can keep notes about users to remember important context from conversations. To update your notes for the current user, include this EXACT JSON structure anywhere in your response (it will be hidden from the user):
{{""ai_note_update"": ""Your notes here (max {MemberProfile.MaxAiNotesLength} chars)""}}

Use this to remember preferences, repeated questions, running jokes, or anything that would help you provide better responses in future conversations. Keep notes concise and relevant. You can update notes at any time. Setting an empty string will clear your notes for this user, and the entire note block will be overwritten with each update.";

        var fallback = GetFallbackResponse();
        return await _ai.GenerateAsync(system, sb.ToString(), fallback, temperature: 0.35f, maxTokens: 250);
    }

    /// <summary>
    /// Detects if the user is asking about weather/forecast.
    /// </summary>
    public bool IsWeatherQuery(string message)
    {
        var lower = message.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\w\s]", " ");

        var patterns = new[]
        {
            @"\b(weather|forecast|rain|snow|temperature|temps|hot|cold|storm)\b",
            @"\b(what's|whats|how's|hows|is it)\b.*\b(weather|forecast)\b",
        };

        foreach (var p in patterns)
        {
            if (Regex.IsMatch(lower, p, RegexOptions.IgnoreCase))
                return true;
        }

        return false;
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
    /// Provides a generic response when the chatbot cannot match a request.
    /// </summary>
    public string GetFallbackResponse()
    {
        return "I'm not sure what you mean. Try asking about the next session, who is DM, or who is bringing food.";
    }

    private static string BuildRotationLine(RotationState rotation)
    {
        if (rotation.Members.Count == 0) return "_not set_";

        var idx = rotation.Index % rotation.Members.Count;
        var ordered = rotation.Members.Skip(idx).Concat(rotation.Members.Take(idx));
        return string.Join(", ", ordered.Select(id => $"<@{id}>")
            .DefaultIfEmpty("_not set_"));
    }

    /// <summary>
    /// Extracts AI note updates from a response and returns the cleaned response.
    /// If an ai_note_update is found, it's extracted and the userId's notes are updated.
    /// </summary>
    public async Task<(string cleanedResponse, string? aiNoteUpdate)> ExtractAndProcessAiNotesAsync(string response, ulong userId)
    {
        // Look for JSON pattern: {"ai_note_update": "..."}
        var pattern = @"\{""ai_note_update""\s*:\s*""([^""]*)""\}";
        var match = Regex.Match(response, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
            return (response, null);

        var noteUpdate = match.Groups[1].Value;
        
        // Update the notes in the database
        await _repo.UpdateAiNotesAsync(userId, string.IsNullOrWhiteSpace(noteUpdate) ? null : noteUpdate);

        // Remove the JSON from the response
        var cleanedResponse = Regex.Replace(response, pattern, "", RegexOptions.IgnoreCase).Trim();

        return (cleanedResponse, noteUpdate);
    }
}

