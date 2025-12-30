using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class IndexModel : PageModel
{
    private readonly BotStatusService _status;

    public IndexModel(BotStatusService status) => _status = status;

    public string State { get; private set; } = "";
    public string? Details { get; private set; }
    public DateTimeOffset LastChangeUtc { get; private set; }

    public void OnGet()
    {
        State = _status.State;
        Details = _status.Details;
        LastChangeUtc = _status.LastChangeUtc;
    }
}
