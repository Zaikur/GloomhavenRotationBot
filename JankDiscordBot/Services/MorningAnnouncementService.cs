using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class MorningAnnouncementService : BackgroundService
{
    private readonly AnnouncementSender _sender;
    private readonly ScheduleService _schedule;
    private readonly AppSettingsService _settings;
    private readonly ILogger<MorningAnnouncementService> _log;

    public MorningAnnouncementService(
        AnnouncementSender sender,
        ScheduleService schedule,
        AppSettingsService settings,
        ILogger<MorningAnnouncementService> log)
    {
        _sender = sender;
        _schedule = schedule;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowLocal = await _schedule.LocalNowAsync();
            var (channelId, hour, minute) = await _settings.GetAnnouncementConfigAsync();

            // If not configured, just idle
            if (channelId == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            // Next run today at HH:mm, else tomorrow
            var targetLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hour, minute, 0);
            if (nowLocal >= targetLocal) targetLocal = targetLocal.AddDays(1);

            var delay = targetLocal - nowLocal;
            _log.LogInformation("MorningAnnouncement: next run at {TargetLocal} (in {Delay})", targetLocal, delay);

            await Task.Delay(delay, stoppingToken);

            try
            {
                var today = DateOnly.FromDateTime(await _schedule.LocalNowAsync());
                var (ok, msg) = await _sender.SendMorningAsync(today, dryRun: false, ct: stoppingToken);

                if (ok) _log.LogInformation("MorningAnnouncement: {Msg}", msg);
                else _log.LogWarning("MorningAnnouncement: {Msg}", msg);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MorningAnnouncement: run failed");
            }
        }
    }
}