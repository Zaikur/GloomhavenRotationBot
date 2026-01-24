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

    [BindProperty] public string AiProvider { get; set; } = ""; // openai | ollama | custom
    [BindProperty] public string AiEndpoint { get; set; } = "";
    [BindProperty] public string AiModel { get; set; } = "";
    [BindProperty] public string? AiApiKey { get; set; }

    [BindProperty] public double? WeatherLatitude { get; set; }
    [BindProperty] public double? WeatherLongitude { get; set; }
    [BindProperty] public string WeatherUnits { get; set; } = "imperial";


    public bool HasToken { get; private set; }
    public string? Message { get; set; }
    public string MessageKind { get; set; } = "info"; // "info" | "success" | "warning" | "danger"

    public async Task OnGetAsync()
    {
        if (TempData.ContainsKey("Message"))
        {
            Message = TempData["Message"]?.ToString();
            MessageKind = TempData["MessageKind"]?.ToString() ?? "info";
        }

        var (token, gid, reg) = await _settings.GetDiscordConfigAsync();

        HasToken = !string.IsNullOrWhiteSpace(token);
        Token = token; // Load actual token
        GuildId = gid == 0 ? "" : gid.ToString();
        RegisterToGuild = reg;
        var (ch, h, m) = await _settings.GetAnnouncementConfigAsync();
        AnnounceChannelId = ch == 0 ? "" : ch.ToString();
        AnnounceTime = $"{h:D2}:{m:D2}";

        AutoAdvanceMinutesAfterStart = await _settings.GetAutoAdvanceMinutesAfterStartAsync();

        var (provider, endpoint, model, apiKey, temp, maxTokens) = await _settings.GetAiConfigAsync();
        AiProvider = provider;
        AiEndpoint = endpoint;
        AiModel = model;
        AiApiKey = apiKey; // Load actual value

        var (lat, lon, units) = await _settings.GetWeatherConfigAsync();
        WeatherLatitude = lat;
        WeatherLongitude = lon;
        WeatherUnits = units;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            TempData["Message"] = "GuildId must be a valid non-zero number.";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        ulong chId = 0;
        if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
            (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
        {
            TempData["Message"] = "Announcement Channel ID must be a valid non-zero number (or leave it blank to disable announcements).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            TempData["Message"] = "Announcement time must be a valid time (HH:mm).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        await _settings.SaveDiscordConfigAsync(Token, gid, RegisterToGuild);
        await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
        await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);

        TempData["Message"] = "Saved. The bot will connect (or reconnect) automatically within a few seconds.";
        TempData["MessageKind"] = "success";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAnnouncementAsync()
    {
        ulong chId = 0;
        if (!string.IsNullOrWhiteSpace(AnnounceChannelId) &&
            (!ulong.TryParse(AnnounceChannelId, out chId) || chId == 0))
        {
            TempData["Message"] = "Announcement Channel ID must be a valid non-zero number (or leave blank to disable).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        if (!TimeOnly.TryParse(AnnounceTime, out var t))
        {
            TempData["Message"] = "Announcement time must be a valid time (HH:mm).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        await _settings.SaveAnnouncementConfigAsync(chId, t.Hour, t.Minute);
        await _settings.SaveAutoAdvanceMinutesAfterStartAsync(AutoAdvanceMinutesAfterStart);

        // Now send preview/test
        var tzRule = await _settings.GetScheduleRuleAsync();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzRule.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(nowLocal);

        var (ok, msg) = await _announcementSender.SendMorningAsync(today, dryRun: true);

        TempData["Message"] = msg;
        TempData["MessageKind"] = ok ? "success" : "warning";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!ulong.TryParse(GuildId, out var gid) || gid == 0)
        {
            TempData["Message"] = "Enter a valid GuildId first.";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        var (storedToken, _, _) = await _settings.GetDiscordConfigAsync();
        var tokenToTest = !string.IsNullOrWhiteSpace(Token) ? Token!.Trim() : storedToken;

        if (string.IsNullOrWhiteSpace(tokenToTest))
        {
            TempData["Message"] = "No token to test. Paste a token (or save one first).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        try
        {
            using var rest = new Discord.Rest.DiscordRestClient();
            await rest.LoginAsync(Discord.TokenType.Bot, tokenToTest);

            var me = await rest.GetCurrentUserAsync();
            var guild = await rest.GetGuildAsync(gid);

            if (guild == null)
            {
                TempData["Message"] = "Token is valid, but the bot cannot access that GuildId. Is the bot invited to that server?";
                TempData["MessageKind"] = "danger";
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Token))
                {
                    await _settings.SaveDiscordConfigAsync(tokenToTest, gid, RegisterToGuild);
                }

                TempData["Message"] = $"Success! Logged in as {me.Username} and can access guild {guild.Name} ({guild.Id})." +
                          (!string.IsNullOrWhiteSpace(Token) ? " Token saved." : "");
                TempData["MessageKind"] = "success";
            }
        }
        catch (Discord.Net.HttpException hex)
        {
            TempData["MessageKind"] = "danger";
            TempData["Message"] = $"Discord API error: {hex.HttpCode} – {hex.Message}";
        }
        catch (Exception ex)
        {
            TempData["MessageKind"] = "danger";
            TempData["Message"] = $"Test failed: {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAiAsync()
    {
        await _settings.SaveAiConfigAsync(AiProvider, AiEndpoint, AiModel, AiApiKey, null, null);
        await _settings.SaveWeatherConfigAsync(WeatherLatitude, WeatherLongitude, WeatherUnits);

        TempData["Message"] = "AI and weather settings saved.";
        TempData["MessageKind"] = "success";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAiConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(AiEndpoint))
        {
            TempData["Message"] = "Enter an endpoint URL first (e.g., http://localhost:11434/v1/chat/completions or https://api.openai.com/v1/chat/completions).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(AiModel))
        {
            TempData["Message"] = "Enter a model name first (e.g., llama3 or gpt-3.5-turbo).";
            TempData["MessageKind"] = "warning";
            return RedirectToPage();
        }

        try
        {
            // Get stored API key if not provided in form
            var (_, _, _, storedApiKey, _, _) = await _settings.GetAiConfigAsync();
            var keyToTest = !string.IsNullOrWhiteSpace(AiApiKey) ? AiApiKey : storedApiKey;

            // Make a simple test request
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var payload = new
            {
                model = AiModel,
                messages = new[] { new { role = "user", content = "Say 'OK' only." } },
                max_tokens = 10
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            if (!string.IsNullOrWhiteSpace(keyToTest))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {keyToTest}");
            }

            var response = await httpClient.PostAsync(AiEndpoint, content);

            if (response.IsSuccessStatusCode)
            {
                TempData["Message"] = $"✓ Successfully connected to {AiEndpoint} with model '{AiModel}'.";
                TempData["MessageKind"] = "success";
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                TempData["Message"] = $"API returned {response.StatusCode}: {errorBody.Substring(0, Math.Min(200, errorBody.Length))}";
                TempData["MessageKind"] = "danger";
            }
        }
        catch (HttpRequestException hex)
        {
            TempData["Message"] = $"Connection failed: {hex.Message}";
            TempData["MessageKind"] = "danger";
        }
        catch (TaskCanceledException)
        {
            TempData["Message"] = "Request timed out (10 seconds). Check the endpoint URL and network connectivity.";
            TempData["MessageKind"] = "danger";
        }
        catch (Exception ex)
        {
            TempData["Message"] = $"Test failed: {ex.Message}";
            TempData["MessageKind"] = "danger";
        }

        return RedirectToPage();
    }

    private async Task ReloadTokenFlagAsync()
    {
        var (token, _, _) = await _settings.GetDiscordConfigAsync();
        HasToken = !string.IsNullOrWhiteSpace(token);
    }
}
