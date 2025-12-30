using Microsoft.AspNetCore.DataProtection;
using GloomhavenRotationBot.Data;

namespace GloomhavenRotationBot.Services;

public sealed class AppSettingsService
{
    private readonly BotRepository _repo;
    private readonly IDataProtector _protector;

    private const string KeyDiscordToken = "Discord.Token.Protected";
    private const string KeyDiscordGuildId = "Discord.GuildId";
    private const string KeyDiscordRegisterToGuild = "Discord.RegisterCommandsToGuild";

    public AppSettingsService(BotRepository repo, IDataProtectionProvider dp)
    {
        _repo = repo;
        _protector = dp.CreateProtector("GloomhavenRotationBot.DiscordToken.v1");
    }

    public async Task<(string? Token, ulong GuildId, bool RegisterToGuild)> GetDiscordConfigAsync()
    {
        var tokenProtected = await _repo.GetSettingAsync(KeyDiscordToken);
        var guildIdStr = await _repo.GetSettingAsync(KeyDiscordGuildId);
        var regStr = await _repo.GetSettingAsync(KeyDiscordRegisterToGuild);

        string? token = null;
        if (!string.IsNullOrWhiteSpace(tokenProtected))
        {
            try { token = _protector.Unprotect(tokenProtected); }
            catch { token = null; } // keys changed or invalid data
        }

        ulong.TryParse(guildIdStr, out var guildId);
        var registerToGuild = !bool.TryParse(regStr, out var b) || b; // default true

        return (string.IsNullOrWhiteSpace(token) ? null : token, guildId, registerToGuild);
    }

    public async Task SaveDiscordConfigAsync(string? token, ulong guildId, bool registerToGuild)
    {
        // Only overwrite token if caller provided one
        if (!string.IsNullOrWhiteSpace(token))
        {
            var protectedToken = _protector.Protect(token.Trim());
            await _repo.UpsertSettingAsync(KeyDiscordToken, protectedToken);
        }

        await _repo.UpsertSettingAsync(KeyDiscordGuildId, guildId.ToString());
        await _repo.UpsertSettingAsync(KeyDiscordRegisterToGuild, registerToGuild.ToString());
    }

    public async Task<bool> HasDiscordConfigAsync()
    {
        var (token, guildId, _) = await GetDiscordConfigAsync();
        return !string.IsNullOrWhiteSpace(token) && guildId > 0;
    }
}
