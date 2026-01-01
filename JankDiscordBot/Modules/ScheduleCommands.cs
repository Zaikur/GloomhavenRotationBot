using System.Globalization;
using System.Linq;
using Discord;
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

    [SlashCommand("cancel", "Cancel an upcoming occurrence")]
    public async Task CancelAsync()
    {
        await DeferAsync(ephemeral: true);

        var list = await GetUpcomingSessionsAsync();
        if (list.Count == 0)
        {
            await FollowupAsync("No upcoming sessions to cancel.", ephemeral: true);
            return;
        }

        var menu = BuildSessionSelectMenu($"sched-cancel:{Context.User.Id}", list, "Select an occurrence to cancel");

        await FollowupAsync("Select an occurrence to cancel:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    [SlashCommand("move", "Move an upcoming occurrence")]
    public async Task MoveAsync()
    {
        await DeferAsync(ephemeral: true);

        var list = await GetUpcomingSessionsAsync();
        if (list.Count == 0)
        {
            await FollowupAsync("No upcoming sessions to move.", ephemeral: true);
            return;
        }

        var menu = BuildSessionSelectMenu($"sched-move:{Context.User.Id}", list, "Select an occurrence to move");

        await FollowupAsync("Select an occurrence to move:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    [SlashCommand("clear", "Clear override (undo cancel/move/note) for an occurrence")]
    public async Task ClearAsync()
    {
        await DeferAsync(ephemeral: true);

        var list = await GetUpcomingSessionsAsync();
        if (list.Count == 0)
        {
            await FollowupAsync("No upcoming sessions to clear.", ephemeral: true);
            return;
        }

        var menu = BuildSessionSelectMenu($"sched-clear:{Context.User.Id}", list, "Select an occurrence to clear");

        await FollowupAsync("Select an occurrence to clear:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    [SlashCommand("preview", "Preview the next morning announcement privately")]
    public async Task PreviewAsync()
    {
        await DeferAsync(ephemeral: true);

        var next = await GetUpcomingSessionsAsync(1);
        if (next.Count == 0)
        {
            await FollowupAsync("No upcoming sessions to preview.", ephemeral: true);
            return;
        }

        var d = DateOnly.FromDateTime(next[0].EffectiveStartLocal);
        var (ok, msg) = await _announcer.BuildMorningTextAsync(d);
        await FollowupAsync(ok ? msg : $"⚠️ {msg}", ephemeral: true);
    }

    private static bool TryParseDate(string s, out DateOnly d)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private async Task<List<SessionInfo>> GetNextSessionsAsync(DateOnly start, int count)
    {
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

    private async Task<List<SessionInfo>> GetUpcomingSessionsAsync(int count = 10)
    {
        var nowLocal = await _schedule.LocalNowAsync();
        return await GetNextSessionsAsync(DateOnly.FromDateTime(nowLocal), count);
    }

    private SelectMenuBuilder BuildSessionSelectMenu(string customId, IEnumerable<SessionInfo> sessions, string placeholder)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(customId)
            .WithPlaceholder(placeholder);

        foreach (var s in sessions)
        {
            var status = s.IsCancelled ? "🛑 Cancelled" : "✅ On";
            var moved = s.OriginalDateLocal != DateOnly.FromDateTime(s.EffectiveStartLocal) ? " (moved)" : "";
            var label = $"{s.EffectiveStartLocal:ddd, MMM d h:mm tt}";
            var desc = $"{status}{moved}";

            menu.AddOption(label, s.OriginalDateLocal.ToString("yyyy-MM-dd"), desc.Length == 0 ? null : desc);
        }

        return menu;
    }

    private bool IsCaller(string userId)
        => Context.User?.Id.ToString() == userId;

    [ComponentInteraction("sched-cancel:*")]
    public async Task HandleCancelSelectionAsync(string userId, string[] selected)
    {
        if (!IsCaller(userId))
        {
            await RespondAsync("That menu isn’t for you.", ephemeral: true);
            return;
        }

        var chosen = selected.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(chosen))
        {
            await RespondAsync("No occurrence selected.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<CancelModal>($"sched-cancel-modal:{userId}:{chosen}");
    }

    [ModalInteraction("sched-cancel-modal:*:*")]
    public async Task HandleCancelModalAsync(string userId, string originalDate, CancelModal modal)
    {
        if (!IsCaller(userId))
        {
            await RespondAsync("That modal isn’t for you.", ephemeral: true);
            return;
        }

        if (!TryParseDate(originalDate, out var d))
        {
            await RespondAsync("Could not parse the selected date.", ephemeral: true);
            return;
        }

        var existing = await _repo.GetSessionOverrideAsync(d);
        var movedTo = existing?.MovedToLocal;

        var who = GetCallerName();
        var newNote = string.IsNullOrWhiteSpace(modal.Reason)
            ? existing?.Note
            : PrefixNote(who, modal.Reason);

        await _repo.UpsertSessionOverrideAsync(new SessionOverrideRow(
            d,
            IsCancelled: true,
            MovedToLocal: movedTo,
            Note: string.IsNullOrWhiteSpace(newNote) ? null : newNote
        ));

        await RespondAsync($"Cancelled occurrence **{d:yyyy-MM-dd}**.", ephemeral: true);
    }

    [ComponentInteraction("sched-move:*")]
    public async Task HandleMoveSelectionAsync(string userId, string[] selected)
    {
        if (!IsCaller(userId))
        {
            await RespondAsync("That menu isn’t for you.", ephemeral: true);
            return;
        }

        var chosen = selected.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(chosen))
        {
            await RespondAsync("No occurrence selected.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<MoveModal>($"sched-move-modal:{userId}:{chosen}");
    }

    [ModalInteraction("sched-move-modal:*:*")]
    public async Task HandleMoveModalAsync(string userId, string originalDate, MoveModal modal)
    {
        if (!IsCaller(userId))
        {
            await RespondAsync("That modal isn’t for you.", ephemeral: true);
            return;
        }

        if (!TryParseDate(originalDate, out var od))
        {
            await RespondAsync("Could not parse the selected date.", ephemeral: true);
            return;
        }

        if (!TryParseDate(modal.NewDate, out var nd))
        {
            await RespondAsync("New date must be `YYYY-MM-DD`.", ephemeral: true);
            return;
        }

        TimeOnly t;
        if (string.IsNullOrWhiteSpace(modal.NewTime))
        {
            var s = await _schedule.GetSessionForOriginalDateAsync(od);
            t = s != null ? TimeOnly.FromDateTime(s.EffectiveStartLocal) : new TimeOnly(18, 30);
        }
        else if (!TimeOnly.TryParseExact(modal.NewTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
        {
            await RespondAsync("Time must be `HH:mm` (24-hour).", ephemeral: true);
            return;
        }

        var moved = nd.ToDateTime(t);
        var existing = await _repo.GetSessionOverrideAsync(od);

        var who = GetCallerName();
        var newNote = string.IsNullOrWhiteSpace(modal.Note)
            ? existing?.Note
            : PrefixNote(who, modal.Note);

        await _repo.UpsertSessionOverrideAsync(new SessionOverrideRow(
            od,
            IsCancelled: existing?.IsCancelled ?? false,
            MovedToLocal: moved,
            Note: string.IsNullOrWhiteSpace(newNote) ? null : newNote
        ));

        await RespondAsync($"Moved occurrence **{od:yyyy-MM-dd}** → **{moved:yyyy-MM-dd h:mm tt}**.", ephemeral: true);
    }

    [ComponentInteraction("sched-clear:*")]
    public async Task HandleClearSelectionAsync(string userId, string[] selected)
    {
        if (!IsCaller(userId))
        {
            await RespondAsync("That menu isn’t for you.", ephemeral: true);
            return;
        }

        var chosen = selected.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(chosen))
        {
            await RespondAsync("No occurrence selected.", ephemeral: true);
            return;
        }

        if (!TryParseDate(chosen, out var d))
        {
            await RespondAsync("Could not parse the selected date.", ephemeral: true);
            return;
        }

        await _repo.DeleteSessionOverrideAsync(d);
        await RespondAsync($"Cleared override for **{d:yyyy-MM-dd}**.", ephemeral: true);
    }

    public sealed class CancelModal : IModal
    {
        public string Title => "Cancel occurrence";

        [InputLabel("Reason (optional)")]
        [ModalTextInput("reason", placeholder: "Illness, travel, etc.", maxLength: 200)]
        public string? Reason { get; set; }
    }

    public sealed class MoveModal : IModal
    {
        public string Title => "Move occurrence";

        [InputLabel("New date (YYYY-MM-DD)")]
        [ModalTextInput("newDate", placeholder: "2025-06-12")]
        public string NewDate { get; set; } = string.Empty;

        [InputLabel("New time (HH:mm, optional)")]
        [ModalTextInput("newTime", placeholder: "18:30", maxLength: 5)]
        public string? NewTime { get; set; }

        [InputLabel("Note (optional)")]
        [ModalTextInput("note", placeholder: "Moved because ...", maxLength: 200, style: TextInputStyle.Paragraph)]
        public string? Note { get; set; }
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
