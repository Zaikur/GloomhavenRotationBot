using Discord;
using Discord.Net;
using Discord.Rest;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SetupModel : PageModel
{
    private readonly AppSettingsService _settings;
    private readonly AnnouncementSender _announcementSender;
    private readonly ScheduleService _schedule;
    private readonly GloomhavenRotationBot.Data.BotRepository _repo;

    public SetupModel(AppSettingsService settings, AnnouncementSender announcementSender, ScheduleService schedule, GloomhavenRotationBot.Data.BotRepository repo)
    {
        _settings = settings;
        _schedule = schedule;
        _repo = repo;
        _announcementSender = announcementSender;
    }

    [BindProperty] public string GuildId { get; set; } = "";
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public bool RegisterToGuild { get; set; } = true;
    [BindProperty] public string? AnnounceChannelId { get; set; } = "";
    [BindProperty] public string AnnounceTime { get; set; } = "09:00"; // "HH:mm"
    [BindProperty] public int AutoAdvanceMinutesAfterStart { get; set; } = 60;
    [BindProperty] public bool ResetPurposePromptHistory { get; set; }

    [BindProperty] public string AnnouncementMode { get; set; } = "EventDay";
    [BindProperty] public DayOfWeek AnnouncementWeekday { get; set; } = DayOfWeek.Monday;
    [BindProperty] public bool CalendarEnabled { get; set; }
    [BindProperty] public string EventTitle { get; set; } = "Gloomhaven game night";
    [BindProperty] public string? EventLocation { get; set; } = "";
    [BindProperty] public string? EventAddress { get; set; } = "";
    [BindProperty] public int EventDuration { get; set; } = 240;
    [BindProperty] public string? EventDescription { get; set; } = "";
    [BindProperty] public int? EventReminder { get; set; } = 30;
    [BindProperty] public bool EventIncludeNote { get; set; } = true;
    [BindProperty] public bool EventIncludeAssignments { get; set; } = true;
    public string AnnouncementPreview { get; set; } = "";
    public string ScheduleTimeZone { get; set; } = "";

    public bool HasToken { get; private set; }
    public string? Message { get; set; }
    public string MessageKind { get; set; } = "info"; // "info" | "success" | "warning" | "danger"

    public async Task OnGetAsync()
    {
        var (token, gid, reg) = await _settings.GetDiscordConfigAsync();

        HasToken = !string.IsNullOrWhiteSpace(token);
        GuildId = gid == 0 ? "" : gid.ToString();
        RegisterToGuild = reg;
        var (ch, h, m) = await _settings.GetAnnouncementConfigAsync();
        AnnounceChannelId = ch == 0 ? "" : ch.ToString();
        AnnounceTime = $"{h:D2}:{m:D2}";

        var options = await _settings.GetAnnouncementOptionsAsync();
        AnnouncementMode = options.Mode;
        AnnouncementWeekday = options.Weekday;
        var calendar = await _settings.GetCalendarOptionsAsync();
        CalendarEnabled = calendar.Enabled;
        EventTitle = calendar.Title;
        EventLocation = calendar.Location;
        EventAddress = calendar.Address;
        EventDuration = calendar.DurationMinutes;
        EventDescription = calendar.Description;
        EventReminder = calendar.ReminderMinutes;
        EventIncludeNote = calendar.IncludeNote;
        EventIncludeAssignments = calendar.IncludeAssignments;
        ScheduleTimeZone = (await _schedule.GetTimeZoneAsync()).Id;
        AnnouncementPreview = await BuildAnnouncementPreviewAsync();
        AutoAdvanceMinutesAfterStart = await _settings.GetAutoAdvanceMinutesAfterStartAsync();
    }

    public async Task<IActionResult> OnPostSaveDiscordAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "GuildId must be a valid non-zero number.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        await _settings.SaveDiscordConfigAsync(Token, gid, RegisterToGuild);
        Message = "Discord settings saved. The bot will connect (or reconnect) automatically within a few seconds.";
        MessageKind = "success";

        Token = null; // never echo back
        await ReloadTokenFlagsAsync();

        await OnGetAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostTestAnnouncementAsync()
    {
        ulong chId = 0;
        if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
            (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
        {
            Message = "Announcement Channel ID must be a valid non-zero number (or leave blank to disable).";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            Message = "Announcement time must be a valid time (HH:mm).";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        if (HasFieldErrors(nameof(AnnouncementMode), nameof(AnnouncementWeekday)) || AnnouncementMode is not ("EventDay" or "Weekday") || !Enum.IsDefined(AnnouncementWeekday))
        {
            Message = "Select a valid announcement mode and weekday.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }
        await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
        await _settings.SaveAnnouncementOptionsAsync(new(AnnouncementMode, AnnouncementWeekday));

        // Send the real next session details without marking it as announced.
        var (ok, msg) = await _announcementSender.SendNextSessionDetailsAsync();

        Message = msg;
        MessageKind = ok ? "success" : "warning";

        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTestDiscordAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "Enter a valid GuildId first.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        var (storedToken, _, _) = await _settings.GetDiscordConfigAsync();
        var tokenToTest = !string.IsNullOrWhiteSpace(Token) ? Token!.Trim() : storedToken;

        if (string.IsNullOrWhiteSpace(tokenToTest))
        {
            Message = "No token to test. Paste a token (or save one first).";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        try
        {
            using var rest = new Discord.Rest.DiscordRestClient();
            await rest.LoginAsync(Discord.TokenType.Bot, tokenToTest);

            var me = await rest.GetCurrentUserAsync();
            var guild = await rest.GetGuildAsync(gid);

            if (guild == null)
            {
                Message = "Token is valid, but the bot cannot access that GuildId. Is the bot invited to that server?";
                MessageKind = "danger";
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Token))
                {
                    await _settings.SaveDiscordConfigAsync(tokenToTest, gid, RegisterToGuild);
                }

                Message = $"Success! Logged in as {me.Username} and can access guild {guild.Name} ({guild.Id})." +
                          (!string.IsNullOrWhiteSpace(Token) ? " Token saved." : "");
                MessageKind = "success";
            }
        }
        catch (Discord.Net.HttpException hex)
        {
            MessageKind = "danger";
            Message = $"Discord API error: {hex.HttpCode} - {hex.Message}";
        }
        catch (Exception ex)
        {
            MessageKind = "danger";
            Message = $"Test failed: {ex.Message}";
        }
        finally
        {
            Token = null; // never echo
            await ReloadTokenFlagsAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAutosaveAsync(string scope)
    {
        try
        {
            switch ((scope ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "announcements":
                {
                    ulong chId = 0;
                    if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
                        (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
                    {
                        return new JsonResult(new { ok = false, message = "Announcement Channel ID must be valid or blank." }) { StatusCode = 400 };
                    }

                    if (!TimeOnly.TryParse(AnnounceTime, out var t))
                    {
                        return new JsonResult(new { ok = false, message = "Announcement time must be HH:mm." }) { StatusCode = 400 };
                    }

                    if (HasFieldErrors(nameof(AnnouncementMode), nameof(AnnouncementWeekday)) || AnnouncementMode is not ("EventDay" or "Weekday") || !Enum.IsDefined(AnnouncementWeekday))
                        return BadRequest(new { ok = false, message = "Select a valid mode and weekday." });
                    await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
                    await _settings.SaveAnnouncementOptionsAsync(new(AnnouncementMode, AnnouncementWeekday));
                    return new JsonResult(new { ok = true, message = "Announcements saved.", preview = await BuildAnnouncementPreviewAsync() });
                }
                case "calendar":
                    if (HasFieldErrors(nameof(CalendarEnabled), nameof(EventTitle), nameof(EventDuration), nameof(EventReminder), nameof(EventIncludeNote), nameof(EventIncludeAssignments)) || string.IsNullOrWhiteSpace(EventTitle) || EventTitle.Length > 200 ||
                        (EventLocation?.Length ?? 0) > 200 || (EventAddress?.Length ?? 0) > 500 ||
                        (EventDescription?.Length ?? 0) > 2000 || EventDuration < 1 || EventDuration > 10080 ||
                        EventReminder is < 0 or > 40320)
                        return BadRequest(new { ok = false, message = "Use a title (max 200 characters), duration 1-10080 minutes, and reminder 0-40320 minutes or blank. Check field lengths." });
                    await _settings.SaveCalendarOptionsAsync(new(CalendarEnabled, EventTitle.Trim(),
                        EventLocation?.Trim() ?? "", EventAddress?.Trim() ?? "", EventDuration,
                        EventDescription?.Trim() ?? "", EventReminder, EventIncludeNote, EventIncludeAssignments));
                    return new JsonResult(new { ok = true, message = "Calendar event settings saved." });
                case "autoadvance":
                    await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);
                    return new JsonResult(new { ok = true, message = "Auto-advance saved." });
                case "bang":
                    if (ResetPurposePromptHistory)
                    {
                        await _settings.ResetPurposePromptSeenAsync();
                    }

                    return new JsonResult(new
                    {
                        ok = true,
                        message = ResetPurposePromptHistory
                            ? "Prompt history reset."
                            : "Bang settings saved."
                    });
                default:
                    return new JsonResult(new { ok = false, message = "Unknown autosave scope." }) { StatusCode = 400 };
            }
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message }) { StatusCode = 500 };
        }
    }

    private bool HasFieldErrors(params string[] fields)
        => fields.Any(field => ModelState.TryGetValue(field, out var entry) && entry.Errors.Count != 0);

    private async Task<string> BuildAnnouncementPreviewAsync()
    {
        var (channel, hour, minute) = await _settings.GetAnnouncementConfigAsync();
        if (channel == 0) return "Announcements disabled: set a channel ID to enable.";
        var options = await _settings.GetAnnouncementOptionsAsync();
        var zone = await _schedule.GetTimeZoneAsync();
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        DateTime? earliest = null;
        SessionInfo? chosen = null;
        for (int i = 0; i < 366; i++)
        {
            foreach (var session in (await _schedule.GetSessionsOccurringOnDateAsync(today.AddDays(i))).DistinctBy(s => s.OccurrenceId))
            {
                var start = AnnouncementPlanner.ToUtc(session.EffectiveStartLocal, zone);
                var target = AnnouncementPlanner.ToUtc(AnnouncementPlanner.TargetLocal(session, options, new TimeOnly(hour, minute)), zone);
                if (start <= now || target >= start || (await _repo.GetMarkersAsync(session.OccurrenceId))?.AnnouncedMorning == true) continue;
                var run = target < now ? now : target;
                if (earliest == null || run < earliest) { earliest = run; chosen = session; }
            }
            // Finish the candidate's week: weekly targets can precede the event date.
            if (chosen != null && today.AddDays(i).DayOfWeek == DayOfWeek.Sunday) break;
        }
        return chosen == null ? "No eligible announcement in the next year. The send time must be before the event starts." :
            $"Next announcement: {TimeZoneInfo.ConvertTimeFromUtc(earliest!.Value, zone):dddd, MMM d, yyyy 'at' h:mm tt} ({zone.Id}), for {chosen.EffectiveStartLocal:dddd, MMM d, yyyy 'at' h:mm tt}.";
    }

    private async Task ReloadTokenFlagsAsync()
    {
        var (token, _, _) = await _settings.GetDiscordConfigAsync();
        HasToken = !string.IsNullOrWhiteSpace(token);
    }
}
