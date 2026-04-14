using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class BroadcastModel : PageModel
{
    private readonly AppSettingsService _settings;
    private readonly AnnouncementSender _announcementSender;
    private readonly BotStatusService _status;

    public BroadcastModel(
        AppSettingsService settings,
        AnnouncementSender announcementSender,
        BotStatusService status)
    {
        _settings = settings;
        _announcementSender = announcementSender;
        _status = status;
    }

    [BindProperty] public string MessageText { get; set; } = "";

    public ulong AnnouncementChannelId { get; private set; }
    public string BotState { get; private set; } = "Unknown";
    public int MaxMessageLength => AnnouncementSender.MaxCustomMessageLength;
    public string? ResultMessage { get; private set; }
    public string ResultKind { get; private set; } = "info";

    public async Task OnGetAsync()
    {
        await LoadPageStateAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadPageStateAsync();

        var (ok, message) = await _announcementSender.SendCustomMessageAsync(MessageText);
        ResultMessage = message;
        ResultKind = ok ? "success" : "warning";

        if (ok)
            MessageText = string.Empty;

        return Page();
    }

    private async Task LoadPageStateAsync()
    {
        var (channelId, _, _) = await _settings.GetAnnouncementConfigAsync();
        AnnouncementChannelId = channelId;
        BotState = _status.State;
    }
}