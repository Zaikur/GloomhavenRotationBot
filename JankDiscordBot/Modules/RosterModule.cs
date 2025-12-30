using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Modules;

[Group("roster", "Manage the DM/Cook rosters")]
public sealed class RosterModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly BotRepository _repo;

    public RosterModule(BotRepository repo) => _repo = repo;

    private static bool IsAdmin(SocketInteractionContext ctx)
    {
        if (ctx.Guild == null) return false;
        if (ctx.User is not SocketGuildUser gu) return false;
        return gu.GuildPermissions.ManageGuild;
    }

    [SlashCommand("list", "Show the roster order for a role.")]
    public async Task ListAsync(RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0)
        {
            await RespondAsync($"Roster for **{role}** is empty.", ephemeral: true);
            return;
        }

        var lines = state.Members
            .Select((id, idx) => $"{(idx == state.Index ? "➡️" : "  ")} {idx + 1}. <@{id}>");

        await RespondAsync($"**{role} roster**\n" + string.Join("\n", lines));
    }

    [SlashCommand("add", "Add a person to the roster (appends to end).")]
    public async Task AddAsync(RotationRole role, [Summary("user", "Person to add")] IUser user)
    {
        if (!IsAdmin(Context))
        {
            await RespondAsync("You need **Manage Server** permission to change rosters.", ephemeral: true);
            return;
        }

        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Contains(user.Id))
        {
            await RespondAsync($"{user.Mention} is already in the **{role}** roster.", ephemeral: true);
            return;
        }

        state.Members.Add(user.Id);
        await _repo.SaveRotationAsync(role, state);

        await RespondAsync($"Added {user.Mention} to **{role}** roster.", ephemeral: true);
    }

    [SlashCommand("remove", "Remove a person from the roster.")]
    public async Task RemoveAsync(RotationRole role, [Summary("user", "Person to remove")] IUser user)
    {
        if (!IsAdmin(Context))
        {
            await RespondAsync("You need **Manage Server** permission to change rosters.", ephemeral: true);
            return;
        }

        var state = await _repo.GetRotationAsync(role);
        var idx = state.Members.IndexOf(user.Id);
        if (idx < 0)
        {
            await RespondAsync($"{user.Mention} is not in the **{role}** roster.", ephemeral: true);
            return;
        }

        state.Members.RemoveAt(idx);

        // keep pointer sane
        if (state.Index >= state.Members.Count) state.Index = 0;

        await _repo.SaveRotationAsync(role, state);
        await RespondAsync($"Removed {user.Mention} from **{role}** roster.", ephemeral: true);
    }

    [SlashCommand("setindex", "Set who is currently 'up' by picking their position (1-based).")]
    public async Task SetIndexAsync(RotationRole role, [Summary("position", "1-based position in roster")] int position)
    {
        if (!IsAdmin(Context))
        {
            await RespondAsync("You need **Manage Server** permission to change rosters.", ephemeral: true);
            return;
        }

        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0)
        {
            await RespondAsync($"Roster for **{role}** is empty.", ephemeral: true);
            return;
        }

        if (position < 1 || position > state.Members.Count)
        {
            await RespondAsync($"Position must be between 1 and {state.Members.Count}.", ephemeral: true);
            return;
        }

        state.Index = position - 1;
        await _repo.SaveRotationAsync(role, state);

        await RespondAsync($"Set **{role}** current turn to position {position}: <@{state.Members[state.Index]}>.", ephemeral: true);
    }
}
