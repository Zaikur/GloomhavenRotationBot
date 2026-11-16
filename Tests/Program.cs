using System.Text;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
var session = new SessionInfo("default:2026-11-19", new(2026, 11, 19), new(2026, 11, 19, 18, 30, 0), false, "Bring snacks");
var weekly = new AppSettingsService.AnnouncementOptions("Weekday", DayOfWeek.Monday);
var sameDay = new AppSettingsService.AnnouncementOptions();
var nine = new TimeOnly(9, 0);
Check(AnnouncementPlanner.TargetLocal(session, weekly, nine) == new DateTime(2026, 11, 16, 9, 0, 0), "Monday of same week");
Check(AnnouncementPlanner.TargetLocal(session, sameDay, nine) == new DateTime(2026, 11, 19, 9, 0, 0), "Event-day target");
var due = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
Check(!AnnouncementPlanner.IsDue(session, weekly, nine, zone, due.AddSeconds(-1)), "Do not send early");
Check(AnnouncementPlanner.IsDue(session, weekly, nine, zone, due), "Send at target");
Check(AnnouncementPlanner.IsDue(session, weekly, nine, zone, due.AddDays(2)), "Catch up after restart");
Check(!AnnouncementPlanner.IsDue(session, weekly, nine, zone, due.AddDays(4)), "Never catch up after start");
Check(!AnnouncementPlanner.IsDue(session, new("Weekday", DayOfWeek.Friday), nine, zone, due.AddDays(4)), "Weekday after event skipped");
Check(!AnnouncementPlanner.IsDue(session, sameDay, new(18, 30), zone, due.AddDays(3)), "Announcement at start skipped");
var sunday = session with { EffectiveStartLocal = new(2027, 1, 3, 18, 30, 0) };
Check(AnnouncementPlanner.TargetLocal(sunday, weekly, nine) == new DateTime(2026, 12, 28, 9, 0, 0), "Sunday stays in prior Monday's week across year");
Check(AnnouncementPlanner.ToUtc(new(2026, 3, 8, 2, 30, 0), zone) == new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc), "DST gap advances to first valid minute");
Check(AnnouncementPlanner.ToUtc(new(2026, 11, 1, 1, 30, 0), zone) == new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), "DST repeated hour chooses first occurrence");

var options = new AppSettingsService.CalendarOptions(true, "Game, night; \\ fun", "Home", "123 Main St", 240,
    "Line one\n" + string.Concat(Enumerable.Repeat("🎲", 60)), 30, true, true);
string Ics(AppSettingsService.CalendarOptions o, SessionInfo? s = null) => Encoding.UTF8.GetString(CalendarEventBuilder.Build(s ?? session, o, zone, "DM: Alice; Food: Bob", due));
var ics = Ics(options);
var unfolded = ics.Replace("\r\n ", "");
Check(unfolded.Contains("DTSTART:20261120T003000Z\r\nDTEND:20261120T043000Z"), "UTC start and duration");
Check(unfolded.Contains("SUMMARY:Game\\, night\\; \\\\ fun"), "iCalendar text escaping");
Check(unfolded.Contains("LOCATION:Home\\, 123 Main St"), "Location and address");
Check(unfolded.Contains("Bring snacks") && unfolded.Contains("DM: Alice"), "Optional event content included");
Check(unfolded.Contains("TRIGGER:-PT30M"), "Reminder alarm");
Check(!Ics(options with { ReminderMinutes = null, IncludeNote = false, IncludeAssignments = false }).Contains("VALARM"), "Reminder disabled");
Check(!Ics(options with { IncludeNote = false, IncludeAssignments = false }).Contains("Bring snacks"), "Notes excluded");
Check(!Ics(options with { IncludeAssignments = false }).Contains("DM: Alice"), "Assignments excluded");
Check(ics.Split("\r\n").All(line => Encoding.UTF8.GetByteCount(line) <= 75), "UTF-8 line folding stays within 75 octets");
Check(ics.EndsWith("END:VCALENDAR\r\n"), "CRLF terminator");
Check(Ics(options, session with { EffectiveStartLocal = session.EffectiveStartLocal.AddDays(1) }).Contains("UID:default:2026-11-19@gloomhavenrotationbot"), "Moved event retains identity");

var db = Path.Combine(Path.GetTempPath(), $"gloomhaven-tests-{Guid.NewGuid():N}.db");
try
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:DbPath"] = db }).Build();
    var repo = new BotRepository(config);
    var settings = new AppSettingsService(repo, NullLogger<AppSettingsService>.Instance);
    var schedule = new ScheduleService(repo, settings);
    Check((await settings.GetAnnouncementOptionsAsync()).Mode == "EventDay", "Existing installations default to event day");
    Check(!(await settings.GetCalendarOptionsAsync()).Enabled, "Calendar opt-in default");
    await settings.SaveAnnouncementOptionsAsync(weekly);
    await settings.SaveCalendarOptionsAsync(options);
    Check(await settings.GetAnnouncementOptionsAsync() == weekly, "Announcement settings round trip");
    Check(await settings.GetCalendarOptionsAsync() == options, "Calendar settings round trip");
    await settings.SaveScheduleRuleAsync(zone.Id, "Weekly", 2, DayOfWeek.Thursday, new(18, 30), 1, new(2026, 11, 19));
    Check((await schedule.GetSessionsOccurringOnDateAsync(new(2026, 11, 26))).Count == 0, "Alternate-week gap skipped");
    await repo.UpsertSessionOverrideAsync(new(new(2026, 11, 19), false, new(2026, 11, 22, 19, 0, 0), "Moved"));
    Check((await schedule.GetSessionsOccurringOnDateAsync(new(2026, 11, 19))).Count == 0, "Moved event removed from original day");
    var moved = (await schedule.GetSessionsOccurringOnDateAsync(new(2026, 11, 22))).Single();
    Check(moved.OccurrenceId == session.OccurrenceId && moved.EffectiveStartLocal.Hour == 19, "Moved event retains occurrence and time");
    await repo.SetAnnouncedAsync(moved.OccurrenceId, due);
    Check((await new BotRepository(config).GetMarkersAsync(moved.OccurrenceId))?.AnnouncedMorning == true, "Delivery marker survives repository restart");
    await repo.UpsertSessionOverrideAsync(new(new(2026, 11, 19), true, new(2026, 11, 22, 19, 0, 0), "Cancelled"));
    Check((await schedule.GetSessionsOccurringOnDateAsync(new(2026, 11, 22))).Single().IsCancelled, "Cancellation retained for announcement");
    await settings.SaveScheduleRuleAsync(zone.Id, "Monthly", 1, DayOfWeek.Thursday, new(18, 30), 3, new(2026, 11, 19));
    Check((await schedule.GetSessionsOccurringOnDateAsync(new(2026, 12, 17))).Count == 1 &&
        (await schedule.GetSessionsOccurringOnDateAsync(new(2026, 12, 10))).Count == 0, "Monthly recurrence skips off weeks");

    var today = DateOnly.FromDateTime(await schedule.LocalNowAsync());
    var targetDate = today.AddDays(2);
    var priorDate = targetDate.AddDays(-7);
    await settings.SaveScheduleRuleAsync(zone.Id, "Weekly", 1, targetDate.DayOfWeek, new(18, 30), 1, targetDate);
    await repo.UpsertSessionOverrideAsync(new(priorDate, false, today.AddDays(1).ToDateTime(new(18, 30)), "Intervening session"));
    await repo.SaveRotationAsync(RotationRole.DM, new() { Members = new() { 1, 2, 3 }, AbsentMembers = new() { 2 } });
    await repo.SaveRotationAsync(RotationRole.Food, new() { Members = new() { 4, 5 } });
    using var client = new Discord.WebSocket.DiscordSocketClient();
    var sender = new AnnouncementSender(client, repo, settings, schedule, NullLogger<AnnouncementSender>.Instance);
    var text = (await sender.BuildMorningTextAsync(targetDate)).Message;
    Check(text.Contains("<@3>") && text.Contains("<@5>"), "Advance announcement projects intervening session and skips absent member");
    Check((await repo.GetRotationAsync(RotationRole.DM)).Index == 0, "Projection never mutates saved rotations");
    await repo.UpsertSessionOverrideAsync(new(priorDate, true, today.AddDays(1).ToDateTime(new(18, 30)), "Cancelled"));
    text = (await sender.BuildMorningTextAsync(targetDate)).Message;
    Check(text.Contains("<@1>") && text.Contains("<@4>"), "Cancelled intervening session does not advance projected assignments");
    Check(!(await sender.BuildMorningTextAsync(targetDate.AddDays(1))).Ok, "No-session preview returns useful result");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Delete(db);
}
Console.WriteLine($"Passed {checks} scheduling, calendar, and persistence checks.");
