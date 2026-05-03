using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class TranscriptHistoryModel : PageModel
{
    private readonly GameplayTranscriptionService _transcription;
    private readonly ScheduleService _schedule;

    public TranscriptHistoryModel(GameplayTranscriptionService transcription, ScheduleService schedule)
    {
        _transcription = transcription;
        _schedule = schedule;
    }

    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    [BindProperty(SupportsGet = true)] public int? Month { get; set; }
    [BindProperty(SupportsGet = true)] public string? Date { get; set; }

    public DateOnly MonthFirst { get; private set; }
    public DateOnly? SelectedDate { get; private set; }
    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    public List<TranscriptDayBucket> Days { get; private set; } = new();

    public async Task OnGetAsync()
    {
        TimeZone = await _schedule.GetTimeZoneAsync();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZone);

        var y = Year ?? nowLocal.Year;
        var m = Month ?? nowLocal.Month;
        MonthFirst = new DateOnly(y, m, 1);

        if (!string.IsNullOrWhiteSpace(Date) && DateOnly.TryParse(Date, out var parsedDate))
            SelectedDate = parsedDate;

        var sessions = await _transcription.GetRecentSessionsAsync(500, HttpContext.RequestAborted);
        var monthStart = MonthFirst;
        var monthEnd = MonthFirst.AddMonths(1).AddDays(-1);

        var filtered = sessions
            .Where(s =>
            {
                var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s.StartedUtc, TimeZone));
                if (SelectedDate.HasValue)
                    return localDay == SelectedDate.Value;

                return localDay >= monthStart && localDay <= monthEnd;
            })
            .OrderByDescending(s => s.StartedUtc)
            .ToList();

        var grouped = filtered
            .GroupBy(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s.StartedUtc, TimeZone)))
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var dayGroup in grouped)
        {
            var scheduled = await _schedule.GetSessionsOccurringOnDateAsync(dayGroup.Key);

            Days.Add(new TranscriptDayBucket
            {
                Date = dayGroup.Key,
                ScheduledSessions = scheduled,
                Transcripts = dayGroup
                    .Select(s => new TranscriptSessionListItem
                    {
                        SessionId = s.SessionId,
                        Status = s.Status,
                        Error = s.Error,
                        StartedLocal = TimeZoneInfo.ConvertTimeFromUtc(s.StartedUtc, TimeZone),
                        EndedLocal = s.EndedUtc.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(s.EndedUtc.Value, TimeZone) : null,
                        ExpectedSpeakers = s.ExpectedSpeakers
                    })
                    .ToList()
            });
        }
    }

    public sealed class TranscriptDayBucket
    {
        public DateOnly Date { get; set; }
        public List<SessionInfo> ScheduledSessions { get; set; } = new();
        public List<TranscriptSessionListItem> Transcripts { get; set; } = new();
    }

    public sealed class TranscriptSessionListItem
    {
        public string SessionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public DateTime StartedLocal { get; set; }
        public DateTime? EndedLocal { get; set; }
        public int ExpectedSpeakers { get; set; }
    }
}