using System.Globalization;
using Discord.Interactions;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;

namespace GloomhavenRotationBot.Discord.Modules;

public sealed class ScheduleCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ScheduleService _schedule;
    private readonly BotRepository _repo;
    private readonly AnnouncementSender _announcer;

    public ScheduleCommands(ScheduleService schedule, BotRepository repo, AnnouncementSender announcer)
    {
        _schedule = schedule;
        _repo = repo;
        _announcer = announcer;
    }

    [SlashCommand("next", "Show the next few sessions")]
    public async Task NextAsync([Summary(description: "How many sessions to show")] int count = 4)
    {
        await DeferAsync(ephemeral: true);
        count = Math.Clamp(count, 1, 12);

        var nowLocal = await _schedule.LocalNowAsync();
        var list = await GetNextSessionsAsync(DateOnly.FromDateTime(nowLocal), count);

        if (list.Count == 0)
        {
            await FollowupAsync("No upcoming sessions found.", ephemeral: true);
            return;
        }

        var lines = new List<string>();
        foreach (var s in list)
        {
            var status = s.IsCancelled ? "🛑 cancelled" : "✅ on";
            var moved = s.OriginalDateLocal != DateOnly.FromDateTime(s.EffectiveStartLocal) ? " (moved)" : "";
            var note = string.IsNullOrWhiteSpace(s.Note) ? "" : $" — {s.Note}";
            lines.Add($"• **{s.EffectiveStartLocal:ddd, MMM d}** @ {s.EffectiveStartLocal:h:mm tt} — {status}{moved}{note}");
        }

        await FollowupAsync(string.Join("\n", lines), ephemeral: true);
    }

    [SlashCommand("cancel", "Cancel a specific occurrence (by original date)")]
    public async Task CancelAsync(
        [Summary(description: "Original date (YYYY-MM-DD)")] string originalDate,
        [Summary(description: "Reason (optional)")] string? reason = null)
    {
        await DeferAsync(ephemeral: true);

        if (!TryParseDate(originalDate, out var d))
        {
            await FollowupAsync("Date must be `YYYY-MM-DD`.", ephemeral: true);
            return;
        }

        var existing = await _repo.GetSessionOverrideAsync(d);

        // Keep any existing move, but mark cancelled.
        var movedTo = existing?.MovedToLocal;

        // If a reason is provided, prefix it with the caller's display name.
        var who = GetCallerName();
        var newNote = string.IsNullOrWhiteSpace(reason)
            ? existing?.Note
            : PrefixNote(who, reason);

        await _repo.UpsertSessionOverrideAsync(new SessionOverrideRow(
            d,
            IsCancelled: true,
            MovedToLocal: movedTo,
            Note: string.IsNullOrWhiteSpace(newNote) ? null : newNote
        ));

        await FollowupAsync($"Cancelled occurrence **{d:yyyy-MM-dd}**.", ephemeral: true);
    }

    [SlashCommand("move", "Move a specific occurrence (by original date) to a new date/time")]
    public async Task MoveAsync(
        [Summary(description: "Original date (YYYY-MM-DD)")] string originalDate,
        [Summary(description: "New date (YYYY-MM-DD)")] string newDate,
        [Summary(description: "New time (HH:mm, optional)")] string? newTime = null,
        [Summary(description: "Note (optional)")] string? note = null)
    {
        await DeferAsync(ephemeral: true);

        if (!TryParseDate(originalDate, out var od) || !TryParseDate(newDate, out var nd))
        {
            await FollowupAsync("Dates must be `YYYY-MM-DD`.", ephemeral: true);
            return;
        }

        TimeOnly t;
        if (string.IsNullOrWhiteSpace(newTime))
        {
            // use the rule’s default time (by asking schedule for the original date)
            var s = await _schedule.GetSessionForOriginalDateAsync(od);
            t = s != null ? TimeOnly.FromDateTime(s.EffectiveStartLocal) : new TimeOnly(18, 30);
        }
        else if (!TimeOnly.TryParseExact(newTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
        {
            await FollowupAsync("Time must be `HH:mm` (24-hour).", ephemeral: true);
            return;
        }

        var moved = nd.ToDateTime(t);

        var existing = await _repo.GetSessionOverrideAsync(od);

        var who = GetCallerName();
        var newNote = string.IsNullOrWhiteSpace(note)
            ? existing?.Note
            : PrefixNote(who, note);

        await _repo.UpsertSessionOverrideAsync(new SessionOverrideRow(
            od,
            IsCancelled: existing?.IsCancelled ?? false,
            MovedToLocal: moved,
            Note: string.IsNullOrWhiteSpace(newNote) ? null : newNote
        ));

        await FollowupAsync($"Moved occurrence **{od:yyyy-MM-dd}** → **{moved:yyyy-MM-dd h:mm tt}**.", ephemeral: true);
    }

    [SlashCommand("clear", "Clear override (undo cancel/move/note) for an occurrence")]
    public async Task ClearAsync([Summary(description: "Original date (YYYY-MM-DD)")] string originalDate)
    {
        await DeferAsync(ephemeral: true);

        if (!TryParseDate(originalDate, out var d))
        {
            await FollowupAsync("Date must be `YYYY-MM-DD`.", ephemeral: true);
            return;
        }

        await _repo.DeleteSessionOverrideAsync(d);
        await FollowupAsync($"Cleared override for **{d:yyyy-MM-dd}**.", ephemeral: true);
    }

    [SlashCommand("preview", "Preview the morning announcement privately (what would be posted)")]
    public async Task PreviewAsync(
        [Summary(description: "Optional date (YYYY-MM-DD). Default is today.")] string? date = null)
    {
        await DeferAsync(ephemeral: true);

        DateOnly d;
        if (string.IsNullOrWhiteSpace(date))
        {
            var nowLocal = await _schedule.LocalNowAsync();
            d = DateOnly.FromDateTime(nowLocal);
        }
        else if (!TryParseDate(date, out d))
        {
            await FollowupAsync("Date must be `YYYY-MM-DD`.", ephemeral: true);
            return;
        }

        var (ok, msg) = await _announcer.BuildMorningTextAsync(d);
        await FollowupAsync(ok ? msg : $"⚠️ {msg}", ephemeral: true);
    }

    private static bool TryParseDate(string s, out DateOnly d)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private async Task<List<SessionInfo>> GetNextSessionsAsync(DateOnly start, int count)
    {
        // brute-force scan forward (fine for small counts)
        var list = new List<SessionInfo>();
        for (int i = 0; i < 366 && list.Count < count; i++)
        {
            var day = start.AddDays(i);
            var sessions = await _schedule.GetSessionsOccurringOnDateAsync(day);
            foreach (var s in sessions)
            {
                // filter out sessions earlier today that already passed
                if (i == 0)
                {
                    var nowLocal = await _schedule.LocalNowAsync();
                    if (s.EffectiveStartLocal < nowLocal) continue;
                }

                list.Add(s);
                if (list.Count >= count) break;
            }
        }
        return list;
    }

    private string GetCallerName()
        => Context.User?.GlobalName
           ?? Context.User?.Username
           ?? "Unknown";

    private static string PrefixNote(string who, string? note)
    {
        note = (note ?? "").Trim();
        if (string.IsNullOrWhiteSpace(note)) return "";

        // Avoid double prefix if user already typed "Name: ..."
        if (note.StartsWith($"{who}:", StringComparison.OrdinalIgnoreCase))
            return note;

        return $"{who}: {note}";
    }
}
