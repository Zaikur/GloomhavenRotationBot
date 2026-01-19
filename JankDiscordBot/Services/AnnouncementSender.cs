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

        var message = BuildBirthdayMessage(displayName, localDate);
        await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

        return (true, "Sent birthday message.");
    }

    private string BuildBirthdayMessage(string displayName, DateOnly localDate)
    {
        // Gloomhaven themed birthday message
        return
            $"🎉 **Happy Birthday, {displayName}!** 🎉\n" +
            $"May your loot be plentiful and your scenarios mercilessly generous — enjoy your special day!\n" +
            $"🗓️ **{localDate:dddd, MMM d}** · From all of us at the Gloomhaven table. 🎲";
    }

    private async Task<string> BuildMessageForSessionAsync(SessionInfo s, CancellationToken ct)
    {
        // CANCELLED
        if (s.IsCancelled)
        {
            var noteLine = string.IsNullOrWhiteSpace(s.Note)
                ? ""
                : $"\n**Reason:** {s.Note.Trim()}";

            // Extra blank line before reason for readability
            return
                $"🛑 **Gloomhaven is cancelled today**\n" +
                $"⏰ *Was scheduled for* **{s.EffectiveStartLocal:h:mm tt}**\n" +
                $"{noteLine}";
        }

        // ACTIVE
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

        // Add blank line between header + assignments for readability
        return
            $"☀️ **Gloomhaven tonight!**\n" +
            $"🗓️ **{s.EffectiveStartLocal:dddd, MMM d}** at **{s.EffectiveStartLocal:h:mm tt}**\n" +
            $"\n" +
            $"**Assignments**\n" +
            $"• 🧙 **DM:** {dmText}\n" +
            $"• 🍕 **Food:** {cookText}" +
            $"{noteBlock}";
    }
}