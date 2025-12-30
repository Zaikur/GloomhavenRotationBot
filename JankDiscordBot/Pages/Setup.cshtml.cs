using Discord;
using Discord.Net;
using Discord.Rest;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SetupModel : PageModel
{
    private readonly AppSettingsService _settings;

    public SetupModel(AppSettingsService settings) => _settings = settings;

    [BindProperty] public string GuildId { get; set; } = "";
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public bool RegisterToGuild { get; set; } = true;

    public bool HasToken { get; private set; }
    public string? Message { get; set; }
    public string MessageKind { get; set; } = "info"; // "info" | "success" | "warning" | "danger"

    public async Task OnGetAsync()
    {
        var (token, gid, reg) = await _settings.GetDiscordConfigAsync();

        HasToken = !string.IsNullOrWhiteSpace(token);
        GuildId = gid == 0 ? "" : gid.ToString();
        RegisterToGuild = reg;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "GuildId must be a valid non-zero number.";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        await _settings.SaveDiscordConfigAsync(Token, gid, RegisterToGuild);

        Message = "Saved. The bot will connect (or reconnect) automatically within a few seconds.";
        MessageKind = "success";

        Token = null; // never echo back
        await ReloadTokenFlagAsync();
        return Page();
    }

    // POST handler for the "Test" button
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "Enter a valid GuildId first.";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        // Use typed token if provided, otherwise use stored token
        var (storedToken, _, _) = await _settings.GetDiscordConfigAsync();
        var tokenToTest = !string.IsNullOrWhiteSpace(Token) ? Token!.Trim() : storedToken;

        if (string.IsNullOrWhiteSpace(tokenToTest))
        {
            Message = "No token to test. Paste a token (or save one first).";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        try
        {
            using var rest = new DiscordRestClient();
            await rest.LoginAsync(TokenType.Bot, tokenToTest);

            // Verifies the token works
            var me = await rest.GetCurrentUserAsync();

            // Verifies the bot can access that guild
            var guild = await rest.GetGuildAsync(gid);
            if (guild == null)
            {
                Message = "Token is valid, but the bot cannot access that GuildId. Is the bot invited to that server?";
                MessageKind = "danger";
            }
            else
            {
                Message = $"Success! Logged in as {me.Username} and can access guild {guild.Name} ({guild.Id}).";
                MessageKind = "success";
            }
        }
        catch (HttpException hex)
        {
            MessageKind = "danger";
            Message = $"Discord API error: {hex.HttpCode} — {hex.Message}";
        }
        catch (Exception ex)
        {
            MessageKind = "danger";
            Message = $"Test failed: {ex.Message}";
        }
        finally
        {
            Token = null; // never echo
            await ReloadTokenFlagAsync();
        }

        return Page();
    }

    private async Task ReloadTokenFlagAsync()
    {
        var (token, _, _) = await _settings.GetDiscordConfigAsync();
        HasToken = !string.IsNullOrWhiteSpace(token);
    }
}
