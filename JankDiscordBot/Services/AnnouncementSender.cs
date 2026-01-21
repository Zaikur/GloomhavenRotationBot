using System.Text;
using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class AnnouncementSender
{
    private readonly DiscordSocketClient _client;
    private readonly BotRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly ScheduleService _schedule;
    private readonly AiTextService _ai;
    private readonly WeatherService _weather;
    private readonly ILogger<AnnouncementSender> _log;
    private readonly Random _random = new();

    private static readonly string[] BirthdayMessages = new[]
    {
        "🎉 **Happy Birthday, {0}!** 🎉\nMay your loot be plentiful and your scenarios mercilessly generous — enjoy your special day! 🎲",
        "🎂 **It's {0}'s birthday!** 🎂\nTime to celebrate another year of surviving Gloomhaven! Here's to more victories and legendary items! 🏆",
        "🎈 **Happy Birthday, {0}!** 🎈\nHere's to a day filled with critical hits, no perils, and all the gold you can carry! 🗺️",
        "🍰 **Cheers to {0} on their birthday!** 🍰\nAnother year, another chance to conquer Gloomhaven. Here's to making it legendary! 💪",
        "🎪 **Happy Birthday, {0}!** 🎪\nMay your abilities be mighty, your spells accurate, and your adventures unforgettable! 🧙",
    };


    public AnnouncementSender(
        DiscordSocketClient client,
        BotRepository repo,
        AppSettingsService settings,
        ScheduleService schedule,
        AiTextService ai,
        WeatherService weather,
        ILogger<AnnouncementSender> log)
    {
        _client = client;
        _repo = repo;
        _settings = settings;
        _schedule = schedule;
        _ai = ai;
        _weather = weather;
        _log = log;
    }

    public async Task<(bool Ok, string Message)> BuildMorningTextAsync(DateOnly localDate, CancellationToken ct = default)
    {
        var sessions = await _schedule.GetSessionsOccurringOnDateAsync(localDate);

        var s = sessions[0];
        var msg = await BuildMessageForSessionAsync(s, ct);

        return (true, msg);
    }

    public async Task<(bool Ok, string Message)> SendMorningAsync(DateOnly localDate, bool dryRun, CancellationToken ct = default)
    {
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();
        if (channelId == 0)
            return (false, "Announcement channel is not set.");

        if (_client.ConnectionState != ConnectionState.Connected)
            return (false, "Discord client is not connected yet (token/guild not ready).");

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
            return (false, "Announcement channel could not be found (check Channel ID).");

        var sessions = await _schedule.GetSessionsOccurringOnDateAsync(localDate);
        if (sessions.Count == 0 && !dryRun)
            return (true, "No session occurs on that date (nothing to announce).");


        // if it's a dry run, build a session so we can send something
        if (dryRun && sessions.Count == 0)
        {
            var testSession = new SessionInfo(
                OccurrenceId: "test:0000-00-00",
                OriginalDateLocal: localDate,
                EffectiveStartLocal: localDate.ToDateTime(new TimeOnly(19, 0)),
                IsCancelled: false,
                Note: "This is a test announcement."
            );

            sessions.Add(testSession);
        }

        int sent = 0;

        foreach (var s in sessions)
        {
            // In normal mode, only announce once per occurrence
            if (!dryRun)
            {
                var markers = await _repo.GetMarkersAsync(s.OccurrenceId);
                if (markers?.AnnouncedMorning == true)
                    continue;
            }

                var message = await BuildMessageForSessionAsync(s, ct);

            await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

            sent++;
            if (!dryRun)
                await _repo.SetAnnouncedAsync(s.OccurrenceId, DateTime.UtcNow);
        }

        return (true, dryRun
            ? $"Test sent {sent} message(s)."
            : $"Sent {sent} morning announcement(s).");
    }

    public async Task<(bool Ok, string Message)> SendBirthdayAsync(ulong userId, string displayName, DateOnly localDate, bool dryRun = false, CancellationToken ct = default)
    {
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();
        if (channelId == 0)
            return (false, "Announcement channel is not set.");

        if (_client.ConnectionState != ConnectionState.Connected)
            return (false, "Discord client is not connected yet (token/guild not ready).");

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
            return (false, "Announcement channel could not be found (check Channel ID).");

        var message = await BuildBirthdayMessageAsync(displayName, localDate, ct);
        await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

        return (true, "Sent birthday message.");
    }

    private async Task<string> BuildBirthdayMessageAsync(string displayName, DateOnly localDate, CancellationToken ct)
    {
        var template = BirthdayMessages[_random.Next(BirthdayMessages.Length)];
        var message = string.Format(template, displayName);
        var fallback = $"{message}\n🗓️ **{localDate:dddd, MMM d}** · From all of us at the Gloomhaven table.";

        var system = "You write cheerful, compact Discord birthday wishes for a small tabletop group. Use the provided display name exactly (it may already include a mention). Keep it 2-4 short lines, be upbeat, and add a small Gloomhaven flavor. Avoid extra Markdown beyond light bold/emoji.";

        var userPrompt = $"Today's birthday: {displayName}. Date: {localDate:dddd, MMM d}. Include the display name once near the top.";

        return await _ai.GenerateAsync(system, userPrompt, fallback, temperature: 0.55f, maxTokens: 120, ct: ct);
    }

    private async Task<string> BuildMessageForSessionAsync(SessionInfo s, CancellationToken ct)
    {
        var dm = await _repo.GetRotationAsync(RotationRole.DM);
        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        string dmText = dm.Members.Count > 0
            ? $"<@{dm.Members[dm.Index % dm.Members.Count]}>"
            : "_(not set)_";

        string cookText = cook.Members.Count > 0
            ? $"<@{cook.Members[cook.Index % cook.Members.Count]}>"
            : "_(not set)_";

        var noteBlock = string.IsNullOrWhiteSpace(s.Note)
            ? ""
            : $"\n\n📝 **Note:** {s.Note.Trim()}";

        var weatherSummary = await _weather.GetDailyForecastSummaryAsync(DateOnly.FromDateTime(s.EffectiveStartLocal), ct);

        string fallback;
        if (s.IsCancelled)
        {
            var noteLine = string.IsNullOrWhiteSpace(s.Note)
                ? ""
                : $"\n**Reason:** {s.Note.Trim()}";

            fallback =
                $"🛑 **Gloomhaven is cancelled today**\n" +
                $"⏰ *Was scheduled for* **{s.EffectiveStartLocal:h:mm tt}**\n" +
                $"{noteLine}" +
                (string.IsNullOrWhiteSpace(weatherSummary) ? "" : $"\n{weatherSummary}");
        }
        else
        {
            fallback =
                $"☀️ **Gloomhaven tonight!**\n" +
                $"🗓️ **{s.EffectiveStartLocal:dddd, MMM d}** at **{s.EffectiveStartLocal:h:mm tt}**\n" +
                $"\n" +
                $"**Assignments**\n" +
                $"• 🧙 **DM:** {dmText}\n" +
                $"• 🍕 **Food:** {cookText}" +
                $"{noteBlock}" +
                (string.IsNullOrWhiteSpace(weatherSummary) ? "" : $"\n{weatherSummary}");
        }

        var system = "You compose concise Discord announcements for a private Gloomhaven group. Keep it friendly, under 6 short lines, and preserve mention strings exactly. Always include date/time and DM/Food assignments when provided. If cancelled, make that the headline. Minimal Markdown only.";

        var prompt = new StringBuilder();
        prompt.AppendLine($"Session date: {s.EffectiveStartLocal:dddd, MMM d} at {s.EffectiveStartLocal:h:mm tt} local time.");
        prompt.AppendLine($"Status: {(s.IsCancelled ? "Cancelled" : "Scheduled")}");
        if (!string.IsNullOrWhiteSpace(s.Note))
            prompt.AppendLine($"Note: {s.Note.Trim()}");
        prompt.AppendLine($"DM: {dmText}");
        prompt.AppendLine($"Food: {cookText}");
        if (!string.IsNullOrWhiteSpace(weatherSummary))
            prompt.AppendLine(weatherSummary);
        prompt.AppendLine("Audience: returning players; be upbeat and clear.");

        return await _ai.GenerateAsync(system, prompt.ToString(), fallback, temperature: 0.4f, maxTokens: 200, ct: ct);
    }
}