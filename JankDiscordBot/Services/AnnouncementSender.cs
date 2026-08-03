using Discord;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class AnnouncementSender
{
    public const int MaxCustomMessageLength = 2000;

    private readonly DiscordSocketClient _client;
    private readonly BotRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly ScheduleService _schedule;
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
        var (channel, error) = await ResolveAnnouncementChannelAsync();
        if (channel == null)
            return (false, error!);

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

    public async Task<(bool Ok, string Message)> SendNextSessionDetailsAsync(CancellationToken ct = default)
    {
        var (channel, error) = await ResolveAnnouncementChannelAsync();
        if (channel == null)
            return (false, error!);

        var nowLocal = await _schedule.LocalNowAsync();
        var session = await _schedule.GetNextSessionAsync(nowLocal);
        if (session == null)
            return (false, "No upcoming session was found.");

        var message = await BuildMessageForSessionAsync(session, ct, nextSessionDetails: true);
        await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

        return (true, "Sent next session details.");
    }

    public async Task<(bool Ok, string Message)> SendBirthdayAsync(ulong userId, string displayName, DateOnly localDate, bool dryRun = false, CancellationToken ct = default)
    {
        var (channel, error) = await ResolveAnnouncementChannelAsync();
        if (channel == null)
            return (false, error!);

        var message = BuildBirthdayMessage(displayName, localDate);
        await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });

        return (true, "Sent birthday message.");
    }

    public async Task<(bool Ok, string Message)> SendCustomMessageAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (false, "Message cannot be empty.");

        if (message.Length > MaxCustomMessageLength)
            return (false, $"Discord only accepts messages up to {MaxCustomMessageLength} characters.");

        var (channel, error) = await ResolveAnnouncementChannelAsync();
        if (channel == null)
            return (false, error!);

        await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });
        return (true, "Sent custom message as GLOM.");
    }

    private string BuildBirthdayMessage(string displayName, DateOnly localDate)
    {
        var template = BirthdayMessages[_random.Next(BirthdayMessages.Length)];
        var message = string.Format(template, displayName);
        return $"{message}\n🗓️ **{localDate:dddd, MMM d}** · From all of us at the Gloomhaven table.";
    }

    private async Task<(IMessageChannel? Channel, string? Error)> ResolveAnnouncementChannelAsync()
    {
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();
        if (channelId == 0)
            return (null, "Announcement channel is not set.");

        if (_client.ConnectionState != ConnectionState.Connected)
            return (null, "Discord client is not connected yet (token/guild not ready).");

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
            return (null, "Announcement channel could not be found (check Channel ID).");

        return (channel, null);
    }

    private async Task<string> BuildMessageForSessionAsync(
        SessionInfo s,
        CancellationToken ct,
        bool nextSessionDetails = false)
    {
        // CANCELLED
        if (s.IsCancelled)
        {
            var noteLine = string.IsNullOrWhiteSpace(s.Note)
                ? ""
                : $"\n**Reason:** {s.Note.Trim()}";

            // Extra blank line before reason for readability
            var cancelledHeading = nextSessionDetails
                ? "🛑 **The next Gloomhaven session is cancelled**\n"
                : "🛑 **Gloomhaven is cancelled today**\n";
            var cancelledTime = nextSessionDetails
                ? $"🗓️ **{s.EffectiveStartLocal:dddd, MMM d}** at **{s.EffectiveStartLocal:h:mm tt}**\n"
                : $"⏰ *Was scheduled for* **{s.EffectiveStartLocal:h:mm tt}**\n";

            return cancelledHeading + cancelledTime + noteLine;
        }

        // ACTIVE
        var dm = await _repo.GetRotationAsync(RotationRole.DM);
        var cook = await _repo.GetRotationAsync(RotationRole.Food);

        var dmId = dm.GetCurrentAvailableMember();
        var cookId = cook.GetCurrentAvailableMember();

        string dmText = dm.Members.Count == 0
            ? "_(not set)_"
            : dmId is null ? "_(all absent)_" : $"<@{dmId.Value}>";

        string cookText = cook.Members.Count == 0
            ? "_(not set)_"
            : cookId is null ? "_(all absent)_" : $"<@{cookId.Value}>";

        var noteBlock = string.IsNullOrWhiteSpace(s.Note)
            ? ""
            : $"\n\n📝 **Note:** {s.Note.Trim()}";

        // Add blank line between header + assignments for readability
        var heading = nextSessionDetails
            ? "🎲 **Next Gloomhaven session**\n"
            : "☀️ **Gloomhaven tonight!**\n";

        return
            heading +
            $"🗓️ **{s.EffectiveStartLocal:dddd, MMM d}** at **{s.EffectiveStartLocal:h:mm tt}**\n" +
            $"\n" +
            $"**Assignments**\n" +
            $"• 🧙 **DM:** {dmText}\n" +
            $"• 🍕 **Food:** {cookText}" +
            $"{noteBlock}";
    }
}
