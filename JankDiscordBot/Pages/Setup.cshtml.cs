using Discord;
using Discord.Net;
using Discord.Rest;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SetupModel : PageModel
{
    private readonly AppSettingsService _settings;
    private readonly AnnouncementSender _announcementSender;

    public SetupModel(AppSettingsService settings, AnnouncementSender announcementSender)
    {
        _settings = settings;
        _announcementSender = announcementSender;
    }

    [BindProperty] public string GuildId { get; set; } = "";
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public bool RegisterToGuild { get; set; } = true;
    [BindProperty] public string AnnounceChannelId { get; set; } = "";
    [BindProperty] public string AnnounceTime { get; set; } = "09:00"; // "HH:mm"
    [BindProperty] public int AutoAdvanceMinutesAfterStart { get; set; } = 60;


    public bool HasToken { get; private set; }
    public string? Message { get; set; }
    public string MessageKind { get; set; } = "info"; // "info" | "success" | "warning" | "danger"

    public async Task OnGetAsync()
    {
        var (token, gid, reg) = await _settings.GetDiscordConfigAsync();

        HasToken = !string.IsNullOrWhiteSpace(token);
        GuildId = gid == 0 ? "" : gid.ToString();
        RegisterToGuild = reg;
        var (ch, h, m) = await _settings.GetAnnouncementConfigAsync();
        AnnounceChannelId = ch == 0 ? "" : ch.ToString();
        AnnounceTime = $"{h:D2}:{m:D2}";

        AutoAdvanceMinutesAfterStart = await _settings.GetAutoAdvanceMinutesAfterStartAsync();
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

        ulong chId = 0;
        if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
            (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
        {
            Message = "Announcement Channel ID must be a valid non-zero number (or leave it blank to disable announcements).";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            Message = "Announcement time must be a valid time (HH:mm).";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        await _settings.SaveDiscordConfigAsync(Token, gid, RegisterToGuild);
        await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
        await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);

        Message = "Saved. The bot will connect (or reconnect) automatically within a few seconds.";
        MessageKind = "success";

        Token = null; // never echo back
        await ReloadTokenFlagAsync();

        await OnGetAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostTestAnnouncementAsync()
    {
        ulong chId = 0;
        if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
            (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
        {
            Message = "Announcement Channel ID must be a valid non-zero number (or leave blank to disable).";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            Message = "Announcement time must be a valid time (HH:mm).";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

        await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
        await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);

        // Now send preview/test
        var tzRule = await _settings.GetScheduleRuleAsync();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzRule.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(nowLocal);

        var (ok, msg) = await _announcementSender.SendMorningAsync(today, dryRun: true);

        Message = msg;
        MessageKind = ok ? "success" : "warning";

        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "Enter a valid GuildId first.";
            MessageKind = "warning";
            await ReloadTokenFlagAsync();
            return Page();
        }

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
            using var rest = new Discord.Rest.DiscordRestClient();
            await rest.LoginAsync(Discord.TokenType.Bot, tokenToTest);

            var me = await rest.GetCurrentUserAsync();
            var guild = await rest.GetGuildAsync(gid);

            if (guild == null)
            {
                Message = "Token is valid, but the bot cannot access that GuildId. Is the bot invited to that server?";
                MessageKind = "danger";
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Token))
                {
                    await _settings.SaveDiscordConfigAsync(tokenToTest, gid, RegisterToGuild);
                }

                Message = $"Success! Logged in as {me.Username} and can access guild {guild.Name} ({guild.Id})." +
                          (!string.IsNullOrWhiteSpace(Token) ? " Token saved." : "");
                MessageKind = "success";
            }
        }
        catch (Discord.Net.HttpException hex)
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
