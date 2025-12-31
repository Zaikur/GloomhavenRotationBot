using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class CalendarModel : PageModel
{
    private readonly BotRepository _repo;
    private readonly ScheduleService _schedule;

    public CalendarModel(BotRepository repo, ScheduleService schedule)
    {
        _repo = repo;
        _schedule = schedule;
    }

    // Query params
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    [BindProperty(SupportsGet = true)] public int? Month { get; set; }
    [BindProperty(SupportsGet = true)] public string? Edit { get; set; }

    // Edit form
    [BindProperty] public bool IsCancelled { get; set; }
    [BindProperty] public string? MoveToDate { get; set; }
    [BindProperty] public string? MoveToTime { get; set; }
    [BindProperty] public string? Note { get; set; }

    public DateOnly MonthFirst { get; private set; }
    public DateOnly GridStart { get; private set; }
    public List<DateOnly> GridDays { get; private set; } = new();
    public DateOnly TodayLocal { get; private set; }

    public Dictionary<DateOnly, List<SessionInfo>> SessionsByDay { get; private set; } = new();

    public SessionInfo? EditingSession { get; private set; }
    public SessionOverrideRow? ExistingOverride { get; private set; }

    public async Task OnGetAsync()
    {
        var nowLocal = await _schedule.LocalNowAsync();
        TodayLocal = DateOnly.FromDateTime(nowLocal);
        var y = Year ?? nowLocal.Year;
        var m = Month ?? nowLocal.Month;

        MonthFirst = new DateOnly(y, m, 1);

        GridStart = StartOfWeek(MonthFirst, DayOfWeek.Monday);
        GridDays = Enumerable.Range(0, 42).Select(i => GridStart.AddDays(i)).ToList();

        await LoadSessionsAsync(GridStart, GridStart.AddDays(41));

        if (!string.IsNullOrWhiteSpace(Edit) && DateOnly.TryParse(Edit, out var original))
        {
            EditingSession = await _schedule.GetSessionForOriginalDateAsync(original);
            ExistingOverride = await _repo.GetSessionOverrideAsync(original);

            if (ExistingOverride != null)
            {
                IsCancelled = ExistingOverride.IsCancelled;
                Note = ExistingOverride.Note;

                if (ExistingOverride.MovedToLocal != null)
                {
                    var dt = ExistingOverride.MovedToLocal.Value;
                    MoveToDate = DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd");
                    MoveToTime = dt.ToString("HH:mm");
                }
            }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Edit) || !DateOnly.TryParse(Edit, out var original))
            return RedirectToPage(new { Year, Month });

        DateTime? movedTo = null;
        if (!string.IsNullOrWhiteSpace(MoveToDate) && DateOnly.TryParse(MoveToDate, out var d))
        {
            var time = TimeOnly.FromTimeSpan(TimeSpan.FromHours(18.5)); // fallback 18:30
            if (!string.IsNullOrWhiteSpace(MoveToTime) && TimeOnly.TryParse(MoveToTime, out var t))
                time = t;

            movedTo = d.ToDateTime(time);
        }

        var row = new SessionOverrideRow(original, IsCancelled, movedTo, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
        await _repo.UpsertSessionOverrideAsync(row);

        return RedirectToPage(new { Year, Month });
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        if (!string.IsNullOrWhiteSpace(Edit) && DateOnly.TryParse(Edit, out var original))
            await _repo.DeleteSessionOverrideAsync(original);

        return RedirectToPage(new { Year, Month });
    }

    private async Task LoadSessionsAsync(DateOnly start, DateOnly end)
    {
        SessionsByDay.Clear();

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var s = await _schedule.GetSessionForOriginalDateAsync(d);
            if (s == null) continue;

            var effDay = DateOnly.FromDateTime(s.EffectiveStartLocal);
            Add(effDay, s);
        }

        var overrides = await _repo.GetOverridesInRangeAsync(start, end);
        foreach (var ov in overrides)
        {
            var s = await _schedule.GetSessionForOriginalDateAsync(ov.OriginalDateLocal);
            if (s == null) continue;

            RemoveOccurrence(s.OccurrenceId);

            var baseStart = new DateTime(ov.OriginalDateLocal.Year, ov.OriginalDateLocal.Month, ov.OriginalDateLocal.Day,
                s.EffectiveStartLocal.Hour, s.EffectiveStartLocal.Minute, 0);

            var effective = ov.MovedToLocal ?? baseStart;

            var rebuilt = new SessionInfo(
                s.OccurrenceId,
                ov.OriginalDateLocal,
                effective,
                ov.IsCancelled,
                ov.Note);

            Add(DateOnly.FromDateTime(rebuilt.EffectiveStartLocal), rebuilt);
        }

        foreach (var kv in SessionsByDay)
            kv.Value.Sort((a, b) => a.EffectiveStartLocal.CompareTo(b.EffectiveStartLocal));

        void Add(DateOnly day, SessionInfo s)
        {
            if (!SessionsByDay.TryGetValue(day, out var list))
                SessionsByDay[day] = list = new List<SessionInfo>();
            list.Add(s);
        }

        void RemoveOccurrence(string occurrenceId)
        {
            foreach (var k in SessionsByDay.Keys.ToList())
            {
                SessionsByDay[k].RemoveAll(x => x.OccurrenceId == occurrenceId);
                if (SessionsByDay[k].Count == 0) SessionsByDay.Remove(k);
            }
        }
    }

    private static DateOnly StartOfWeek(DateOnly d, DayOfWeek startDay)
    {
        int norm = ((int)d.DayOfWeek + 6) % 7;
        int startNorm = ((int)startDay + 6) % 7;

        int delta = (norm - startNorm + 7) % 7;
        return d.AddDays(-delta);
    }
}
