using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

public sealed class ScheduleService
{
    private readonly BotRepository _repo;
    private readonly AppSettingsService _settings;

    public ScheduleService(BotRepository repo, AppSettingsService settings)
    {
        _repo = repo;
        _settings = settings;
    }

    public async Task<TimeZoneInfo> GetTimeZoneAsync()
    {
        var rule = await _settings.GetScheduleRuleAsync();
        return TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZoneId);
    }

    public async Task<DateTime> LocalNowAsync()
    {
        var tz = await GetTimeZoneAsync();
        return TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
    }

    public async Task<SessionInfo?> GetSessionForOriginalDateAsync(DateOnly originalDate)
    {
        var rule = await _settings.GetScheduleRuleAsync();
        if (!IsOriginalOccurrenceDate(originalDate, rule)) return null;

        var baseStart = originalDate.ToDateTime(rule.Time);

        var ov = await _repo.GetSessionOverrideAsync(originalDate);
        var cancelled = ov?.IsCancelled ?? false;
        var movedTo = ov?.MovedToLocal;
        var note = ov?.Note;

        var effective = movedTo ?? baseStart;

        return new SessionInfo(
            OccurrenceId: $"default:{originalDate:yyyy-MM-dd}",
            OriginalDateLocal: originalDate,
            EffectiveStartLocal: effective,
            IsCancelled: cancelled,
            Note: note
        );
    }

    public async Task<List<SessionInfo>> GetSessionsOccurringOnDateAsync(DateOnly date)
    {
        var list = new List<SessionInfo>();

        var normal = await GetSessionForOriginalDateAsync(date);
        if (normal != null && DateOnly.FromDateTime(normal.EffectiveStartLocal) == date)
            list.Add(normal);

        var moved = await _repo.GetOverridesMovedToDateAsync(date);
        foreach (var ov in moved)
        {
            var s = await GetSessionForOriginalDateAsync(ov.OriginalDateLocal);
            if (s == null) continue;
            if (DateOnly.FromDateTime(s.EffectiveStartLocal) == date)
                list.Add(s);
        }

        list.Sort((a, b) => a.EffectiveStartLocal.CompareTo(b.EffectiveStartLocal));
        return list;
    }

    private static bool IsOriginalOccurrenceDate(DateOnly d, AppSettingsService.ScheduleRule rule)
    {
        return rule.Frequency switch
        {
            "Monthly" => IsMonthly(d, rule),
            _ => IsWeekly(d, rule),
        };
    }

    private static bool IsWeekly(DateOnly d, AppSettingsService.ScheduleRule rule)
    {
        if (d.DayOfWeek != rule.DayOfWeek) return false;

        var anchor = AlignToDowOnOrBefore(rule.AnchorDate, rule.DayOfWeek);

        var days = d.DayNumber - anchor.DayNumber; // multiple of 7
        var weeks = days / 7;

        var mod = Mod(weeks, rule.Interval);
        return mod == 0;
    }

    private static bool IsMonthly(DateOnly d, AppSettingsService.ScheduleRule rule)
    {
        var occ = NthWeekdayOfMonth(d.Year, d.Month, rule.DayOfWeek, rule.MonthlyWeek);
        if (d != occ) return false;

        var anchorOcc = NthWeekdayOfMonth(rule.AnchorDate.Year, rule.AnchorDate.Month, rule.DayOfWeek, rule.MonthlyWeek);

        var months = (d.Year - anchorOcc.Year) * 12 + (d.Month - anchorOcc.Month);
        var mod = Mod(months, rule.Interval);
        return mod == 0;
    }

    private static DateOnly AlignToDowOnOrBefore(DateOnly date, DayOfWeek dow)
    {
        var delta = ((int)date.DayOfWeek - (int)dow + 7) % 7;
        return date.AddDays(-delta);
    }

    private static int Mod(int value, int modulus)
    {
        var m = value % modulus;
        return m < 0 ? m + modulus : m;
    }

    private static DateOnly NthWeekdayOfMonth(int year, int month, DayOfWeek dow, int week)
    {
        if (week == -1)
        {
            // last weekday of month
            var lastDay = DateTime.DaysInMonth(year, month);
            var d = new DateOnly(year, month, lastDay);
            var delta = ((int)d.DayOfWeek - (int)dow + 7) % 7;
            return d.AddDays(-delta);
        }

        week = Math.Clamp(week, 1, 5);

        var first = new DateOnly(year, month, 1);
        var deltaForward = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        var firstDow = first.AddDays(deltaForward);

        var target = firstDow.AddDays((week - 1) * 7);

        if (target.Month != month)
            return NthWeekdayOfMonth(year, month, dow, -1);

        return target;
    }
}