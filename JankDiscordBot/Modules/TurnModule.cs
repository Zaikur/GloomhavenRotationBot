using Discord.Interactions;
using GloomhavenRotationBot.Data;

namespace JankDiscordBot.Modules;

public sealed class TurnModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly BotRepository _repo;

    public TurnModule(BotRepository repo) => _repo = repo;

    [SlashCommand("turn", "Ask whose turn it is for DM or Cook.")]
    public async Task TurnAsync(
        [Summary("role", "dm or cook")] RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0)
        {
            await RespondAsync($"No roster set for **{role}** yet. Use `/roster add {role} @person`.", ephemeral: true);
            return;
        }

        var idx = Math.Clamp(state.Index, 0, state.Members.Count - 1);
        var userId = state.Members[idx];

        await RespondAsync($"For **{role}**: it’s <@{userId}> (next: <@{state.Members[(idx + 1) % state.Members.Count]}>).");
    }

    [SlashCommand("advance", "Advance the rotation to the next person (use after the session).")]
    public async Task AdvanceAsync(
        [Summary("role", "dm or cook")] RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0)
        {
            await RespondAsync($"No roster set for **{role}** yet.", ephemeral: true);
            return;
        }

        state.Index = (state.Index + 1) % state.Members.Count;
        await _repo.SaveRotationAsync(role, state);

        var userId = state.Members[state.Index];
        await RespondAsync($"Advanced **{role}**. Up now: <@{userId}>.");
    }

    [SlashCommand("cant", "If the current person can't do it, swap them with the next person for this cycle.")]
    public async Task CantAsync(
        [Summary("role", "dm or cook")] RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count < 2)
        {
            await RespondAsync($"Need at least 2 people in the **{role}** roster to swap.", ephemeral: true);
            return;
        }

        var i = state.Index;
        var j = (i + 1) % state.Members.Count;

        // swap current with next
        (state.Members[i], state.Members[j]) = (state.Members[j], state.Members[i]);

        await _repo.SaveRotationAsync(role, state);

        await RespondAsync($"Swapped. For **{role}**, up now: <@{state.Members[state.Index]}> (next: <@{state.Members[(state.Index + 1) % state.Members.Count]}>).");
    }
}