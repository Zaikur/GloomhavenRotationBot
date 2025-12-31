using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using GloomhavenRotationBot.Services;

public class ScheduleModel : PageModel
{
    private readonly AppSettingsService _settings;

    public ScheduleModel(AppSettingsService settings) => _settings = settings;

    [BindProperty] public string TimeZoneId { get; set; } = "America/Chicago";
    [BindProperty] public string Frequency { get; set; } = "Weekly";
    [BindProperty] public int Interval { get; set; } = 1;
    [BindProperty] public int DayOfWeekValue { get; set; } = (int)DayOfWeek.Monday;
    [BindProperty] public string Time { get; set; } = "18:30";
    [BindProperty] public int MonthlyWeek { get; set; } = 1;

    public string? Message { get; private set; }
    public string MessageKind { get; private set; } = "info";

    public async Task OnGetAsync()
    {
        var rule = await _settings.GetScheduleRuleAsync();
        TimeZoneId = rule.TimeZoneId;
        Frequency = rule.Frequency;
        Interval = rule.Interval;
        DayOfWeekValue = (int)rule.DayOfWeek;
        Time = rule.Time.ToString("HH:mm");
        MonthlyWeek = rule.MonthlyWeek;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Frequency = Frequency == "Monthly" ? "Monthly" : "Weekly";
        Interval = Math.Max(1, Interval);

        if (!TimeOnly.TryParse(Time, out var t))
        {
            Message = "Time must be a valid time (HH:mm).";
            MessageKind = "warning";
            await OnGetAsync();
            return Page();
        }

        var dow = (DayOfWeek)Math.Clamp(DayOfWeekValue, 0, 6);

        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
        var anchor = DateOnly.FromDateTime(nowLocal);

        await _settings.SaveScheduleRuleAsync(
            timeZoneId: TimeZoneId,
            frequency: Frequency,
            interval: Interval,
            dayOfWeek: dow,
            time: t,
            monthlyWeek: MonthlyWeek,
            anchorDate: anchor
        );

        Message = "Saved schedule.";
        MessageKind = "success";
        return RedirectToPage("/Schedule");
    }
}