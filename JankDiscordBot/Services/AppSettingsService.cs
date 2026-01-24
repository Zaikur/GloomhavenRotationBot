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
    private const string DiscordTokenKey = "Discord:Token";
    private const string DiscordGuildKey = "Discord:GuildId";
    private const string DiscordRegKey = "Discord:RegisterCommandsToGuild";

    private const string KeyAiProvider = "AI.Provider";          // openai | ollama | custom
    private const string KeyAiEndpoint = "AI.Endpoint";          // URL to chat completions API
    private const string KeyAiModel = "AI.Model";                // model name
    private const string KeyAiApiKey = "AI.ApiKey";              // optional
    private const string KeyAiTemperature = "AI.Temperature";    // optional override
    private const string KeyAiMaxTokens = "AI.MaxTokens";        // optional override
    private const string KeyWeatherLat = "Weather.Latitude";
    private const string KeyWeatherLon = "Weather.Longitude";
    private const string KeyWeatherUnits = "Weather.Units";      // imperial | metric


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
        var tokenRaw = (await _repo.GetSettingAsync(DiscordTokenKey))?.Trim() ?? "";
        var gidStr = (await _repo.GetSettingAsync(DiscordGuildKey))?.Trim() ?? "";
        var regStr = (await _repo.GetSettingAsync(DiscordRegKey))?.Trim() ?? "true";

        ulong.TryParse(gidStr, out var gid);
        var reg = regStr.Equals("true", StringComparison.OrdinalIgnoreCase) || regStr == "1";

        // Check if token is encrypted (Data Protection prefix is "CfDJ8")
        // If encrypted, decrypt it; otherwise use as-is (legacy plain text)
        string token = "";
        if (!string.IsNullOrWhiteSpace(tokenRaw))
        {
            if (tokenRaw.StartsWith("CfDJ8", StringComparison.Ordinal))
            {
                try
                {
                    token = _protector.Unprotect(tokenRaw);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to decrypt Discord token");
                }
            }
            else
            {
                // Legacy plain text token
                token = tokenRaw;
            }
        }

        return (token, gid, reg);
    }

    public async Task SaveDiscordConfigAsync(string? tokenPlain, ulong guildId, bool registerToGuild)
    {
        await _repo.UpsertSettingAsync(DiscordGuildKey, guildId.ToString());
        await _repo.UpsertSettingAsync(DiscordRegKey, registerToGuild ? "true" : "false");

        // Encrypt and store token if user supplied one
        if (!string.IsNullOrWhiteSpace(tokenPlain))
        {
            var encrypted = _protector.Protect(tokenPlain.Trim());
            await _repo.UpsertSettingAsync(DiscordTokenKey, encrypted);
        }
    }

    public async Task<bool> HasDiscordConfigAsync()
    {
        var (token, guildId, _) = await GetDiscordConfigAsync();
        return !string.IsNullOrWhiteSpace(token) && guildId > 0;
    }

    public async Task<(string Provider, string Endpoint, string Model, string ApiKey, double? Temperature, int? MaxTokens)> GetAiConfigAsync()
    {
        var provider = (await _repo.GetSettingAsync(KeyAiProvider)) ?? "";
        var endpoint = (await _repo.GetSettingAsync(KeyAiEndpoint)) ?? "";
        var model = (await _repo.GetSettingAsync(KeyAiModel)) ?? "";
        var apiKey = (await _repo.GetSettingAsync(KeyAiApiKey)) ?? "";
        var tempStr = await _repo.GetSettingAsync(KeyAiTemperature);
        var maxStr = await _repo.GetSettingAsync(KeyAiMaxTokens);

        double? temp = null;
        if (double.TryParse(tempStr, out var tParsed)) temp = tParsed;

        int? max = null;
        if (int.TryParse(maxStr, out var mParsed)) max = mParsed;

        return (provider, endpoint, model, apiKey, temp, max);
    }

    public async Task SaveAiConfigAsync(string provider, string endpoint, string model, string? apiKey, double? temperature = null, int? maxTokens = null)
    {
        provider = string.IsNullOrWhiteSpace(provider) ? "" : provider.Trim();
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? "" : endpoint.Trim();
        model = string.IsNullOrWhiteSpace(model) ? "" : model.Trim();

        await _repo.UpsertSettingAsync(KeyAiProvider, provider);
        await _repo.UpsertSettingAsync(KeyAiEndpoint, endpoint);
        await _repo.UpsertSettingAsync(KeyAiModel, model);

        if (temperature.HasValue)
            await _repo.UpsertSettingAsync(KeyAiTemperature, temperature.Value.ToString("0.###"));
        if (maxTokens.HasValue)
            await _repo.UpsertSettingAsync(KeyAiMaxTokens, Math.Max(1, maxTokens.Value).ToString());

        if (!string.IsNullOrWhiteSpace(apiKey))
            await _repo.UpsertSettingAsync(KeyAiApiKey, apiKey.Trim());
    }

    public async Task<(double? Latitude, double? Longitude, string Units)> GetWeatherConfigAsync()
    {
        double? lat = null;
        double? lon = null;

        var latStr = await _repo.GetSettingAsync(KeyWeatherLat);
        if (double.TryParse(latStr, out var dlat)) lat = dlat;

        var lonStr = await _repo.GetSettingAsync(KeyWeatherLon);
        if (double.TryParse(lonStr, out var dlon)) lon = dlon;

        var units = (await _repo.GetSettingAsync(KeyWeatherUnits)) ?? "imperial";
        units = units.Equals("metric", StringComparison.OrdinalIgnoreCase) ? "metric" : "imperial";

        return (lat, lon, units);
    }

    public async Task SaveWeatherConfigAsync(double? latitude, double? longitude, string units)
    {
        var unitsNorm = units.Equals("metric", StringComparison.OrdinalIgnoreCase) ? "metric" : "imperial";

        await _repo.UpsertSettingAsync(KeyWeatherUnits, unitsNorm);
        await _repo.UpsertSettingAsync(KeyWeatherLat, latitude?.ToString() ?? "");
        await _repo.UpsertSettingAsync(KeyWeatherLon, longitude?.ToString() ?? "");
    }
}
