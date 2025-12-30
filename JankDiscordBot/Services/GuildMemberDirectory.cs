using Discord.WebSocket;

namespace GloomhavenRotationBot.Services;

public sealed class GuildMemberDirectory
{
    private readonly DiscordSocketClient _client;
    private readonly AppSettingsService _settings;

    public GuildMemberDirectory(DiscordSocketClient client, AppSettingsService settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<List<(ulong Id, string Name)>> GetMembersAsync()
    {
        var (_, guildId, _) = await _settings.GetDiscordConfigAsync();
        if (guildId == 0) return new();

        var guild = _client.GetGuild(guildId);
        if (guild == null) return new();

        var selfId = _client.CurrentUser?.Id;

        return guild.Users
            .Where(u =>
                !u.IsBot &&                 // hide all bots
                (selfId == null || u.Id != selfId.Value))
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(u => (u.Id, u.DisplayName))
            .ToList();
    }
}
