using System.Globalization;
using System.Text;

namespace GloomhavenRotationBot.Services;

public static class CalendarEventBuilder
{
    public static byte[] Build(SessionInfo session, AppSettingsService.CalendarOptions options,
        TimeZoneInfo zone, string assignments, DateTime utcNow)
    {
        var start = AnnouncementPlanner.ToUtc(session.EffectiveStartLocal, zone);
        var description = options.Description;
        if (options.IncludeNote && !string.IsNullOrWhiteSpace(session.Note)) description += "\n" + session.Note;
        if (options.IncludeAssignments) description += "\n" + assignments;
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//GloomhavenRotationBot//Sessions//EN",
            "CALSCALE:GREGORIAN", "BEGIN:VEVENT", "UID:" + session.OccurrenceId + "@gloomhavenrotationbot",
            "DTSTAMP:" + Stamp(utcNow), "DTSTART:" + Stamp(start),
            "DTEND:" + Stamp(start.AddMinutes(options.DurationMinutes)), "SUMMARY:" + Escape(options.Title),
            "LOCATION:" + Escape(string.Join(", ", new[] { options.Location, options.Address }.Where(s => !string.IsNullOrWhiteSpace(s)))),
            "DESCRIPTION:" + Escape(description.Trim()) };
        if (options.ReminderMinutes is int minutes)
            lines.AddRange(new[] { "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Event reminder",
                $"TRIGGER:-PT{minutes}M", "END:VALARM" });
        lines.AddRange(new[] { "END:VEVENT", "END:VCALENDAR" });
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Select(Fold)) + "\r\n");
    }

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n")
        .Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Fold(string line)
    {
        var result = new StringBuilder();
        int bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 75) { result.Append("\r\n "); bytes = 1; }
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
