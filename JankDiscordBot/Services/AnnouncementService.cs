using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

public sealed class AnnouncementService(AnnouncementSender sender, ScheduleService schedule,
    AppSettingsService settings, BotRepository repo, ILogger<AnnouncementService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (channel, hour, minute) = await settings.GetAnnouncementConfigAsync();
                if (channel != 0)
                {
                    var zone = await schedule.GetTimeZoneAsync();
                    var now = DateTime.UtcNow;
                    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
                    var options = await settings.GetAnnouncementOptionsAsync();
                    // All remaining events this week, including moved occurrences.
                    var days = 7 - (((int)today.DayOfWeek + 6) % 7);
                    for (int i = 0; i < days; i++)
                    foreach (var session in (await schedule.GetSessionsOccurringOnDateAsync(today.AddDays(i))).DistinctBy(s => s.OccurrenceId))
                    {
                        if (!AnnouncementPlanner.IsDue(session, options, new TimeOnly(hour, minute), zone, now)) continue;
                        if ((await repo.GetMarkersAsync(session.OccurrenceId))?.AnnouncedMorning == true) continue;
                        var (ok, message) = await sender.SendSessionAsync(session, false, stoppingToken);
                        if (!ok) log.LogWarning("Announcement: {Message}", message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Announcement check failed; will retry"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
