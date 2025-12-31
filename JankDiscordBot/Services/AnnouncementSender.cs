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
    private readonly ILogger<AnnouncementSender> _log;

    public AnnouncementSender(
        DiscordSocketClient client,
        BotRepository repo,
        AppSettingsService settings,
        ScheduleService schedule,
        ILogger<AnnouncementSender> log)
    {
        _client = client;
        _repo = repo;
        _settings = settings;
        _schedule = schedule;
        _log = log;
    }

    public async Task<(bool Ok, string Message)> BuildMorningTextAsync(DateOnly localDate, CancellationToken ct = default)
    {
        var sessions = await _schedule.GetSessionsOccurringOnDateAsync(localDate);
        if (sessions.Count == 0)
            return (true, "No session occurs on that date (nothing to announce).");

        // For preview, just show the first session that occurs that day
        var s = sessions[0];

        if (s.IsCancelled)
        {
            var noteLine = string.IsNullOrWhiteSpace(s.Note) ? "" : $"\n📝 **Reason:** {s.Note.Trim()}";
            return (true,
                $"🛑 **Gloomhaven** is **cancelled** for today.\n" +
                $"(Scheduled time was {s.EffectiveStartLocal: h:mm tt})" +
                noteLine);
        }

        var dm = await _repo.GetRotationAsync(RotationRole.DM);
        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        string dmText = dm.Members.Count > 0 ? $"<@{dm.Members[dm.Index % dm.Members.Count]}>" : "_(not set)_";
        string cookText = cook.Members.Count > 0 ? $"<@{cook.Members[cook.Index % cook.Members.Count]}>" : "_(not set)_";

        var noteLine2 = string.IsNullOrWhiteSpace(s.Note) ? "" : $"\n📝 {s.Note.Trim()}";

        var message =
            $"☀️ **Gloomhaven tonight!**\n" +
            $"🕡 **When:** {s.EffectiveStartLocal:dddd, MMM d} at {s.EffectiveStartLocal: h:mm tt}\n" +
            $"🧙 **DM:** {dmText}\n" +
            $"🍕 **Food:** {cookText}{noteLine2}";

        return (true, message);
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
        if (sessions.Count == 0)
            return (true, "No session occurs on that date (nothing to announce).");

        int sent = 0;
        foreach (var s in sessions)
        {
            if (!dryRun)
            {
                var markers = await _repo.GetMarkersAsync(s.OccurrenceId);
                if (markers?.AnnouncedMorning == true)
                    continue;
            }

            if (s.IsCancelled)
            {
                var noteLn = string.IsNullOrWhiteSpace(s.Note)
                    ? ""
                    : $"\n📝 **Reason:** {s.Note.Trim()}";

                await channel.SendMessageAsync(
                    $"🛑 **Gloomhaven** is **cancelled** for today.\n" +
                    $"(Scheduled time was {s.EffectiveStartLocal: h:mm tt})" +
                    noteLn,
                    options: new RequestOptions { CancelToken = ct });

                sent++;
                if (!dryRun)
                    await _repo.SetAnnouncedAsync(s.OccurrenceId, DateTime.UtcNow);

                continue;
            }

            var dm = await _repo.GetRotationAsync(RotationRole.DM);
            var cook = await _repo.GetRotationAsync(RotationRole.Food);

            string dmText = dm.Members.Count > 0 ? $"<@{dm.Members[dm.Index % dm.Members.Count]}>" : "_(not set)_";
            string cookText = cook.Members.Count > 0 ? $"<@{cook.Members[cook.Index % cook.Members.Count]}>" : "_(not set)_";
            var noteLine = string.IsNullOrWhiteSpace(s.Note) ? "" : $"\n📝 {s.Note}";

            var message =
                $"☀️ **Gloomhaven tonight!**\n" +
                $"🕡 **When:** {s.EffectiveStartLocal:dddd, MMM d} at {s.EffectiveStartLocal: h:mm tt}\n" +
                $"🧙 **DM:** {dmText}\n" +
                $"🍕 **Food:** {cookText}{noteLine}";

            await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

            sent++;
            if (!dryRun)
                await _repo.SetAnnouncedAsync(s.OccurrenceId, DateTime.UtcNow);
        }

        return (true, dryRun
            ? $"Test sent {sent} message(s)."
            : $"Sent {sent} morning announcement(s).");
    }
}