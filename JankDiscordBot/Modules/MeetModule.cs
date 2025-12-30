using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;
using Microsoft.Extensions.Configuration;

namespace GloomhavenRotationBot.Modules;

public enum MeetStatus
{
    yes = 0,
    no = 1
}

[Group("meet", "Meeting schedule commands")]
public sealed class MeetModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly BotRepository _repo;
    private readonly IConfiguration _config;

    public MeetModule(BotRepository repo, IConfiguration config)
    {
        _repo = repo;
        _config = config;
    }

    private static bool IsAdmin(SocketInteractionContext ctx)
    {
        if (ctx.Guild == null) return false;
        if (ctx.User is not SocketGuildUser gu) return false;
        return gu.GuildPermissions.ManageGuild;
    }

    [SlashCommand("next", "Show the next scheduled session (considering overrides).")]
    public async Task NextAsync()
    {
        var tz = GetTz();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime;

        foreach (var dt in NextOccurrences(nowLocal, weeks: 12))
        {
            var date = DateOnly.FromDateTime(dt);
            var isMeet = await IsMeetingAsync(date);
            if (isMeet)
            {
                await RespondAsync($"Next session: **{dt:dddd, MMM d, yyyy} @ {dt:h:mm tt}** ✅");
                return;
            }
        }

        await RespondAsync("No meeting found in the next 12 weeks (check overrides).", ephemeral: true);
    }

    [SlashCommand("list", "List upcoming weeks with meeting/cancel status.")]
    public async Task ListAsync([Summary("weeks", "How many weeks ahead")] int weeks = 6)
    {
        weeks = Math.Clamp(weeks, 1, 26);

        var tz = GetTz();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime;

        var occurrences = NextOccurrences(nowLocal, weeks).ToList();
        if (occurrences.Count == 0)
        {
            await RespondAsync("No occurrences generated (config issue).", ephemeral: true);
            return;
        }

        var lines = new List<string>();
        foreach (var dt in occurrences)
        {
            var date = DateOnly.FromDateTime(dt);
            var ov = await _repo.GetOverrideAsync(date);
            var isMeet = ov?.IsMeeting ?? true;

            var mark = isMeet ? "✅" : "❌";
            var note = string.IsNullOrWhiteSpace(ov?.Note) ? "" : $" — _{ov!.Note}_";
            lines.Add($"{mark} **{dt:MMM d, yyyy}** @ {dt:h:mm tt}{note}");
        }

        await RespondAsync("**Upcoming sessions**\n" + string.Join("\n", lines));
    }

    [SlashCommand("set", "Override a specific date as meeting/cancelled.")]
    public async Task SetAsync(
        [Summary("date", "YYYY-MM-DD")] string date,
        [Summary("status", "yes/no")] MeetStatus status,
        [Summary("note", "Optional note")] string? note = null)
    {
        if (!IsAdmin(Context))
        {
            await RespondAsync("You need **Manage Server** permission to edit the schedule.", ephemeral: true);
            return;
        }

        if (!DateOnly.TryParse(date, out var d))
        {
            await RespondAsync("Date must be in format **YYYY-MM-DD**.", ephemeral: true);
            return;
        }

        var isMeeting = status == MeetStatus.yes;
        await _repo.UpsertOverrideAsync(d, isMeeting, note);

        await RespondAsync($"Set **{d:yyyy-MM-dd}** to {(isMeeting ? "✅ meeting" : "❌ cancelled")}.{(string.IsNullOrWhiteSpace(note) ? "" : $" Note: {note}")}", ephemeral: true);
    }

    private async Task<bool> IsMeetingAsync(DateOnly date)
    {
        var ov = await _repo.GetOverrideAsync(date);
        return ov?.IsMeeting ?? true; // default weekly meeting is ON
    }

    private TimeZoneInfo GetTz()
    {
        var tzId = _config["Scheduling:TimeZoneId"] ?? "America/Chicago";
        return TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    private IEnumerable<DateTime> NextOccurrences(DateTime nowLocal, int weeks)
    {
        // Default: Mondays @ 18:30
        var dayName = _config["Scheduling:DefaultMeetingDay"] ?? "Monday";
        var hour = int.TryParse(_config["Scheduling:DefaultMeetingHour"], out var h) ? h : 18;
        var minute = int.TryParse(_config["Scheduling:DefaultMeetingMinute"], out var m) ? m : 30;

        if (!Enum.TryParse<DayOfWeek>(dayName, ignoreCase: true, out var targetDow))
            targetDow = DayOfWeek.Monday;

        // Find the next target day/time (including today if still before time)
        var candidateDate = nowLocal.Date;
        var candidate = candidateDate.AddHours(hour).AddMinutes(minute);

        int addDays = ((int)targetDow - (int)candidate.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(addDays);

        // If it's today but already past the meeting time, go to next week
        if (candidate <= nowLocal)
            candidate = candidate.AddDays(7);

        for (int i = 0; i < weeks; i++)
            yield return candidate.AddDays(7 * i);
    }
}
