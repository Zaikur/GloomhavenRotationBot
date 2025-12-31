using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class AutoAdvanceService : BackgroundService
{
    private readonly BotRepository _repo;
    private readonly AppSettingsService _settings;
    private readonly ScheduleService _schedule;
    private readonly ILogger<AutoAdvanceService> _log;

    public AutoAdvanceService(
        BotRepository repo,
        AppSettingsService settings,
        ScheduleService schedule,
        ILogger<AutoAdvanceService> log)
    {
        _repo = repo;
        _settings = settings;
        _schedule = schedule;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check periodically
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AutoAdvance tick failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowLocal = await _schedule.LocalNowAsync();
        var graceMinutes = await _settings.GetAutoAdvanceMinutesAfterStartAsync();
        var cutoffLocal = nowLocal.AddMinutes(-graceMinutes);

        var today = DateOnly.FromDateTime(nowLocal);
        var yesterday = today.AddDays(-1);

        var candidates = new List<SessionInfo>();
        candidates.AddRange(await _schedule.GetSessionsOccurringOnDateAsync(today));
        candidates.AddRange(await _schedule.GetSessionsOccurringOnDateAsync(yesterday));

        var eligible = candidates
            .Where(s => !s.IsCancelled && s.EffectiveStartLocal <= cutoffLocal)
            .OrderBy(s => s.EffectiveStartLocal)
            .ToList();

        foreach (var s in eligible)
        {
            var markers = await _repo.GetMarkersAsync(s.OccurrenceId);
            if (markers?.Advanced == true) continue; // already advanced

            _log.LogInformation("AutoAdvance: advancing rotations for occurrence {OccurrenceId} at {StartLocal}",
                s.OccurrenceId, s.EffectiveStartLocal);

            await AdvanceRotationAsync(RotationRole.DM);
            await AdvanceRotationAsync(RotationRole.Food);

            await _repo.SetAdvancedAsync(s.OccurrenceId, DateTime.UtcNow);
        }
    }

    private async Task AdvanceRotationAsync(RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0) return;

        state.Index = (state.Index + 1) % state.Members.Count;
        await _repo.SaveRotationAsync(role, state);
    }
}
