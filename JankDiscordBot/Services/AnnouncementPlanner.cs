namespace GloomhavenRotationBot.Services;

public static class AnnouncementPlanner
{
    public static bool IsDue(SessionInfo session, AppSettingsService.AnnouncementOptions options,
        TimeOnly time, TimeZoneInfo zone, DateTime utcNow)
    {
        var target = ToUtc(TargetLocal(session, options, time), zone);
        var start = ToUtc(session.EffectiveStartLocal, zone);
        return target <= utcNow && start > utcNow && target < start;
    }

    public static DateTime TargetLocal(SessionInfo session, AppSettingsService.AnnouncementOptions options, TimeOnly time)
    {
        var date = DateOnly.FromDateTime(session.EffectiveStartLocal);
        if (options.Mode == "Weekday")
        {
            var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
            date = monday.AddDays(((int)options.Weekday + 6) % 7);
        }
        return date.ToDateTime(time);
    }

    // Move nonexistent spring-forward times to the first valid minute. For the
    // repeated autumn hour choose its first occurrence, consistently everywhere.
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
