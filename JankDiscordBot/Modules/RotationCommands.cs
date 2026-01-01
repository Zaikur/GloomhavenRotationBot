using Discord.Interactions;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Discord.Modules;

public sealed class RotationCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly BotRepository _repo;

    public RotationCommands(BotRepository repo)
    {
        _repo = repo;
    }

    [SlashCommand("who", "Who is up next? (DM or Food)")]
    public async Task WhoAsync([Summary(description: "Role: dm or food")] string role)
    {
        await DeferAsync(ephemeral: true);

        var rr = ParseRole(role);
        if (rr is null)
        {
            await FollowupAsync("Role must be `dm` or `food`.", ephemeral: true);
            return;
        }

        var state = await _repo.GetRotationAsync(rr.Value);
        if (state.Members.Count == 0)
        {
            await FollowupAsync($"No members set for **{rr}** yet. Add them in the Web UI.", ephemeral: true);
            return;
        }

        var id = state.Members[state.Index % state.Members.Count];
        await FollowupAsync($"**{Pretty(rr.Value)}:** <@{id}>", ephemeral: true);
    }

    [SlashCommand("advance", "Manually advance the rotation (dm, food, or both)")]
    public async Task AdvanceAsync([Summary(description: "Role: dm, food, or all")] string role)
    {
        await DeferAsync(ephemeral: true);

        if (role.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            await AdvanceOneAsync(RotationRole.DM);
            await AdvanceOneAsync(RotationRole.Food);
            await FollowupAsync("Advanced **DM** and **Food**.", ephemeral: true);
            return;
        }

        var rr = ParseRole(role);
        if (rr is null)
        {
            await FollowupAsync("Role must be `dm`, `food`, or `all`.", ephemeral: true);
            return;
        }

        await AdvanceOneAsync(rr.Value);
        await FollowupAsync($"Advanced **{Pretty(rr.Value)}**.", ephemeral: true);
    }

    private async Task AdvanceOneAsync(RotationRole role)
    {
        var state = await _repo.GetRotationAsync(role);
        if (state.Members.Count == 0) return;

        state.Index = (state.Index + 1) % state.Members.Count;
        await _repo.SaveRotationAsync(role, state);
    }

    private static RotationRole? ParseRole(string role)
    {
        if (role.Equals("dm", StringComparison.OrdinalIgnoreCase)) return RotationRole.DM;
        if (role.Equals("food", StringComparison.OrdinalIgnoreCase) || role.Equals("cook", StringComparison.OrdinalIgnoreCase)) return RotationRole.Food;
        return null;
    }

    private static string Pretty(RotationRole role) => role == RotationRole.DM ? "DM" : "Food";
}