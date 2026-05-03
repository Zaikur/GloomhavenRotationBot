using Discord;
using Discord.Net;
using Discord.Rest;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net;
using System.Net.Http.Headers;

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
    [BindProperty] public bool ResetPurposePromptHistory { get; set; }
    [BindProperty] public string? HuggingFaceToken { get; set; }

    public bool HasToken { get; private set; }
    public bool HasHuggingFaceToken { get; private set; }
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
        HasHuggingFaceToken = await _settings.HasHuggingFaceTokenAsync();
    }

    public async Task<IActionResult> OnPostSaveDiscordAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "GuildId must be a valid non-zero number.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        await _settings.SaveDiscordConfigAsync(Token, gid, RegisterToGuild);
        Message = "Discord settings saved. The bot will connect (or reconnect) automatically within a few seconds.";
        MessageKind = "success";

        Token = null; // never echo back
        await ReloadTokenFlagsAsync();

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
            await ReloadTokenFlagsAsync();
            return Page();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            Message = "Announcement time must be a valid time (HH:mm).";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
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

    public async Task<IActionResult> OnPostTestDiscordAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            Message = "Enter a valid GuildId first.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        var (storedToken, _, _) = await _settings.GetDiscordConfigAsync();
        var tokenToTest = !string.IsNullOrWhiteSpace(Token) ? Token!.Trim() : storedToken;

        if (string.IsNullOrWhiteSpace(tokenToTest))
        {
            Message = "No token to test. Paste a token (or save one first).";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
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
            Message = $"Discord API error: {hex.HttpCode} - {hex.Message}";
        }
        catch (Exception ex)
        {
            MessageKind = "danger";
            Message = $"Test failed: {ex.Message}";
        }
        finally
        {
            Token = null; // never echo
            await ReloadTokenFlagsAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveHuggingFaceAsync()
    {
        if (string.IsNullOrWhiteSpace(HuggingFaceToken))
        {
            Message = "No Hugging Face token entered. Existing token unchanged.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            await OnGetAsync();
            return Page();
        }

        await _settings.SaveHuggingFaceTokenAsync(HuggingFaceToken);
        HuggingFaceToken = null;
        Message = "Hugging Face token saved.";
        MessageKind = "success";
        await ReloadTokenFlagsAsync();
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTestHuggingFaceAsync()
    {
        var storedToken = await _settings.GetHuggingFaceTokenAsync();
        var tokenToTest = !string.IsNullOrWhiteSpace(HuggingFaceToken) ? HuggingFaceToken!.Trim() : storedToken;

        if (string.IsNullOrWhiteSpace(tokenToTest))
        {
            Message = "No Hugging Face token to test. Paste one or save one first.";
            MessageKind = "warning";
            await ReloadTokenFlagsAsync();
            return Page();
        }

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenToTest);

            var whoami = await http.GetAsync("https://huggingface.co/api/whoami-v2");
            if (!whoami.IsSuccessStatusCode)
            {
                Message = "Token was rejected by Hugging Face. Confirm token type/scope and try again.";
                MessageKind = "danger";
                await ReloadTokenFlagsAsync();
                return Page();
            }

            var modelAccess = await http.GetAsync("https://huggingface.co/pyannote/speaker-diarization-3.1/resolve/main/README.md");
            if (modelAccess.IsSuccessStatusCode)
            {
                Message = "Hugging Face token test succeeded, including pyannote model access.";
                MessageKind = "success";
            }
            else if (modelAccess.StatusCode == HttpStatusCode.Unauthorized || modelAccess.StatusCode == HttpStatusCode.Forbidden)
            {
                Message = "Token is valid, but pyannote model access was denied. Accept the model terms on Hugging Face first.";
                MessageKind = "warning";
            }
            else
            {
                Message = $"Token is valid, but pyannote model access check returned {(int)modelAccess.StatusCode}.";
                MessageKind = "warning";
            }
        }
        catch (Exception ex)
        {
            Message = $"Hugging Face test failed: {ex.Message}";
            MessageKind = "danger";
        }
        finally
        {
            HuggingFaceToken = null;
            await ReloadTokenFlagsAsync();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAutosaveAsync(string scope)
    {
        try
        {
            switch ((scope ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "announcements":
                {
                    ulong chId = 0;
                    if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
                        (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
                    {
                        return new JsonResult(new { ok = false, message = "Announcement Channel ID must be valid or blank." }) { StatusCode = 400 };
                    }

                    if (!TimeOnly.TryParse(AnnounceTime, out var t))
                    {
                        return new JsonResult(new { ok = false, message = "Announcement time must be HH:mm." }) { StatusCode = 400 };
                    }

                    await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
                    return new JsonResult(new { ok = true, message = "Announcements saved." });
                }
                case "autoadvance":
                    await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);
                    return new JsonResult(new { ok = true, message = "Auto-advance saved." });
                case "bang":
                    if (ResetPurposePromptHistory)
                    {
                        await _settings.ResetPurposePromptSeenAsync();
                    }

                    return new JsonResult(new
                    {
                        ok = true,
                        message = ResetPurposePromptHistory
                            ? "Prompt history reset."
                            : "Bang settings saved."
                    });
                default:
                    return new JsonResult(new { ok = false, message = "Unknown autosave scope." }) { StatusCode = 400 };
            }
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message }) { StatusCode = 500 };
        }
    }

    private async Task ReloadTokenFlagsAsync()
    {
        var (token, _, _) = await _settings.GetDiscordConfigAsync();
        HasToken = !string.IsNullOrWhiteSpace(token);
        HasHuggingFaceToken = await _settings.HasHuggingFaceTokenAsync();
    }
}
