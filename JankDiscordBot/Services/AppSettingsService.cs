using Microsoft.AspNetCore.DataProtection;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

public sealed class AppSettingsService
{
    private readonly BotRepository _repo;
    private readonly IDataProtector _protector;
    private readonly ILogger<AppSettingsService> _log;

    private const string KeyAnnounceChannelId = "Discord.AnnounceChannelId";
    private const string KeyAnnounceHour = "Announcements.Hour";
    private const string KeyAnnounceMinute = "Announcements.Minute";
    private const string KeyAutoAdvanceMinutesAfterStart = "Scheduling.AutoAdvanceMinutesAfterStart";
    private const string DiscordTokenKey = "discord.token";
    private const string DiscordGuildKey = "discord.guildId";
    private const string DiscordRegKey = "discord.registerToGuild";


    public AppSettingsService(BotRepository repo, IDataProtectionProvider dp, ILogger<AppSettingsService> log)
    {
        _repo = repo;
        _log = log;
        _protector = dp.CreateProtector("GloomhavenRotationBot.DiscordToken.v1");
    }

    private const string KeyTzId = "Scheduling.TimeZoneId";
    private const string KeyFreq = "Scheduling.Frequency";              // Weekly | Monthly
    private const string KeyInterval = "Scheduling.Interval";            // 1,2,3...
    private const string KeyDow = "Scheduling.DayOfWeek";                // 0..6 (Sunday..Saturday)
    private const string KeyTime = "Scheduling.Time";                    // "HH:mm"
    private const string KeyMonthlyWeek = "Scheduling.MonthlyWeek";      // 1..5 or -1 = Last
    private const string KeyAnchorDate = "Scheduling.AnchorDate";        // "yyyy-MM-dd" (for interval alignment)

    public sealed record ScheduleRule(
        string TimeZoneId,
        string Frequency,
        int Interval,
        DayOfWeek DayOfWeek,
        TimeOnly Time,
        int MonthlyWeek,      // 1..5 or -1 (Last)
        DateOnly AnchorDate);

    public async Task<ScheduleRule> GetScheduleRuleAsync()
    {
        var tz = (await _repo.GetSettingAsync(KeyTzId)) ?? "America/Chicago";
        var freq = (await _repo.GetSettingAsync(KeyFreq)) ?? "Weekly";

        var interval = int.TryParse(await _repo.GetSettingAsync(KeyInterval), out var i) ? Math.Max(1, i) : 1;
        var dow = int.TryParse(await _repo.GetSettingAsync(KeyDow), out var d) ? (DayOfWeek)Math.Clamp(d, 0, 6) : DayOfWeek.Monday;

        var timeStr = (await _repo.GetSettingAsync(KeyTime)) ?? "18:30";
        if (!TimeOnly.TryParse(timeStr, out var time)) time = new TimeOnly(18, 30);

        var mw = int.TryParse(await _repo.GetSettingAsync(KeyMonthlyWeek), out var w) ? w : 1;
        if (mw == 0) mw = 1;
        if (mw < -1) mw = -1;
        if (mw > 5) mw = 5;

        var anchorStr = await _repo.GetSettingAsync(KeyAnchorDate);
        if (!DateOnly.TryParse(anchorStr, out var anchor))
        {
            // default anchor to "today" in tz
            var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz));
            anchor = DateOnly.FromDateTime(nowLocal);
        }

        return new ScheduleRule(tz, freq, interval, dow, time, mw, anchor);
    }

    public async Task SaveScheduleRuleAsync(
        string timeZoneId,
        string frequency,
        int interval,
        DayOfWeek dayOfWeek,
        TimeOnly time,
        int monthlyWeek,
        DateOnly? anchorDate = null)
    {
        frequency = frequency is "Monthly" ? "Monthly" : "Weekly";
        interval = Math.Max(1, interval);

        if (monthlyWeek == 0) monthlyWeek = 1;
        if (monthlyWeek < -1) monthlyWeek = -1;
        if (monthlyWeek > 5) monthlyWeek = 5;

        // set/keep anchor date for interval alignment
        var anchor = anchorDate ?? (await GetScheduleRuleAsync()).AnchorDate;

        await _repo.UpsertSettingAsync(KeyTzId, timeZoneId);
        await _repo.UpsertSettingAsync(KeyFreq, frequency);
        await _repo.UpsertSettingAsync(KeyInterval, interval.ToString());
        await _repo.UpsertSettingAsync(KeyDow, ((int)dayOfWeek).ToString());
        await _repo.UpsertSettingAsync(KeyTime, time.ToString("HH:mm"));
        await _repo.UpsertSettingAsync(KeyMonthlyWeek, monthlyWeek.ToString());
        await _repo.UpsertSettingAsync(KeyAnchorDate, anchor.ToString("yyyy-MM-dd"));
    }

    public async Task<(ulong ChannelId, int Hour, int Minute)> GetAnnouncementConfigAsync()
    {
        var chStr = await _repo.GetSettingAsync(KeyAnnounceChannelId);
        ulong.TryParse(chStr, out var channelId);

        var hStr = await _repo.GetSettingAsync(KeyAnnounceHour);
        var mStr = await _repo.GetSettingAsync(KeyAnnounceMinute);

        var hour = int.TryParse(hStr, out var h) ? h : 9;
        var minute = int.TryParse(mStr, out var m) ? m : 0;

        return (channelId, hour, minute);
    }

    public async Task<int> GetAutoAdvanceMinutesAfterStartAsync()
    {
        var s = await _repo.GetSettingAsync(KeyAutoAdvanceMinutesAfterStart);
        return int.TryParse(s, out var v) ? v : 60; // default: advance 60 minutes after start
    }

    public async Task SaveAutoAdvanceMinutesAfterStartAsync(int minutes)
    {
        if (minutes < 0) minutes = 0;
        await _repo.UpsertSettingAsync(KeyAutoAdvanceMinutesAfterStart, minutes.ToString());
    }

    public async Task SaveAnnouncementConfigAsync(ulong channelId, int hour, int minute)
    {
        await _repo.UpsertSettingAsync(KeyAnnounceChannelId, channelId == 0 ? "" : channelId.ToString());
        await _repo.UpsertSettingAsync(KeyAnnounceHour, hour.ToString());
        await _repo.UpsertSettingAsync(KeyAnnounceMinute, minute.ToString());
    }

    public async Task<(string Token, ulong GuildId, bool RegisterToGuild)> GetDiscordConfigAsync()
    {
        var token = (await _repo.GetSettingAsync("Discord:Token"))?.Trim() ?? "";
        var gidStr = (await _repo.GetSettingAsync("Discord:GuildId"))?.Trim() ?? "";
        var regStr = (await _repo.GetSettingAsync("Discord:RegisterCommandsToGuild"))?.Trim() ?? "true";

        ulong.TryParse(gidStr, out var gid);
        var reg = regStr.Equals("true", StringComparison.OrdinalIgnoreCase) || regStr == "1";

        if (token.StartsWith("CfDJ8", StringComparison.Ordinal))
            token = "";

        return (token, gid, reg);
    }

    public async Task SaveDiscordConfigAsync(string? tokenPlain, ulong guildId, bool registerToGuild)
    {
        await _repo.UpsertSettingAsync("Discord:GuildId", guildId.ToString());
        await _repo.UpsertSettingAsync("Discord:RegisterCommandsToGuild", registerToGuild ? "true" : "false");

        // only overwrite token if user supplied one
        if (!string.IsNullOrWhiteSpace(tokenPlain))
            await _repo.UpsertSettingAsync("Discord:Token", tokenPlain.Trim());
    }

    public async Task<bool> HasDiscordConfigAsync()
    {
        var (token, guildId, _) = await GetDiscordConfigAsync();
        return !string.IsNullOrWhiteSpace(token) && guildId > 0;
    }
}
