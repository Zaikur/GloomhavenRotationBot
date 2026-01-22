using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class BirthdayService : BackgroundService
{
    private readonly BotRepository _repo;
    private readonly AnnouncementSender _sender;
    private readonly ScheduleService _schedule;
    private readonly GuildMemberDirectory _directory;
    private readonly ILogger<BirthdayService> _log;

    public BirthdayService(
        BotRepository repo,
        AnnouncementSender sender,
        ScheduleService schedule,
        GuildMemberDirectory directory,
        ILogger<BirthdayService> log)
    {
        _repo = repo;
        _sender = sender;
        _schedule = schedule;
        _directory = directory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        DateOnly lastRun = DateOnly.MinValue;

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var nowLocal = await _schedule.LocalNowAsync();
                var today = DateOnly.FromDateTime(nowLocal);

                // Run once per day at/after 9:00am if it hasn't sent yet today.
                if (nowLocal.Hour >= 9 && lastRun != today)
                {
                    await RunForTodayAsync(today, stoppingToken);
                    lastRun = today;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BirthdayService tick failed");
            }
        }
    }

    private async Task RunForTodayAsync(DateOnly today, CancellationToken ct)
    {
        var list = await _repo.GetAllBirthdaysAsync();
        if (list.Count == 0) return;

        // map user ids to names
        var members = await _directory.GetMembersAsync();
        var nameMap = members.ToDictionary(x => x.Id, x => x.Name);

        foreach (var b in list)
        {
            if (b.Month == today.Month && b.Day == today.Day)
            {
                // avoid duplicate sends if already sent this year
                if (b.LastSentYear == today.Year) continue;

                var display = nameMap.TryGetValue(b.UserId, out var n) ? n : $"<@{b.UserId}>";
                _log.LogInformation("Sending birthday for {User} ({UserId})", display, b.UserId);

                try
                {
                    await _sender.SendBirthdayAsync(b.UserId, display, today, dryRun: false, ct: ct);
                    await _repo.SetBirthdaySentYearAsync(b.UserId, today.Year);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to send birthday for {UserId}", b.UserId);
                }
            }
        }
    }
}
