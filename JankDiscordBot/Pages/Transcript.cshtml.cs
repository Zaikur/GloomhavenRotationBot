using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json.Serialization;

public class TranscriptModel : PageModel
{
    private readonly GameplayTranscriptionService _transcription;
    private readonly AppSettingsService _settings;
    private readonly GuildMemberDirectory _members;

    public TranscriptModel(
        GameplayTranscriptionService transcription,
        AppSettingsService settings,
        GuildMemberDirectory members)
    {
        _transcription = transcription;
        _settings = settings;
        _members = members;
    }

    [BindProperty] public int ExpectedSpeakers { get; set; } = 4;
    [BindProperty] public string CommandTemplate { get; set; } = string.Empty;
    [BindProperty] public string RootPath { get; set; } = "data/transcripts";

    public string? FlashMessage { get; private set; }
    public string FlashKind { get; private set; } = "info";

    public List<TranscriptSessionState> Sessions { get; private set; } = new();
    public TranscriptSessionState? ActiveSession { get; private set; }
    public TranscriptSessionState? SelectedSession { get; private set; }
    public string SelectedSessionId { get; private set; } = string.Empty;

    public List<TranscriptSegment> Segments { get; private set; } = new();
    public List<string> Speakers { get; private set; } = new();
    public Dictionary<string, TranscriptSpeakerAssignment> SpeakerAssignments { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(ulong Id, string Name)> GuildMembers { get; private set; } = new();

    public GameplayTranscriptionService.ModelCacheState ModelCacheState { get; private set; }
    public string? ModelDownloadError { get; private set; }

    public string DisplaySpeaker(string speaker)
    {
        if (SpeakerAssignments.TryGetValue(speaker, out var assignment))
        {
            if (!string.IsNullOrWhiteSpace(assignment.PlayerName))
                return assignment.PlayerName!;

            if (assignment.PlayerId.HasValue)
            {
                var user = GuildMembers.FirstOrDefault(m => m.Id == assignment.PlayerId.Value);
                if (user.Id != 0)
                    return user.Name;
            }
        }

        return speaker;
    }

    public async Task OnGetAsync(string? sessionId, string? message, string? kind)
    {
        FlashMessage = message;
        FlashKind = kind is "success" or "warning" or "danger" ? kind : "info";
        await LoadAsync(sessionId, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostDownloadModelsAsync()
    {
        await _transcription.StartModelDownloadAsync();
        return new JsonResult(new { ok = true, state = "Downloading" });
    }

    public IActionResult OnGetModelStatusAsync()
    {
        var state = _transcription.GetModelCacheState();
        var error = _transcription.GetModelDownloadError();
        return new JsonResult(new { state = state.ToString(), error });
    }

    public async Task<IActionResult> OnPostSaveConfigAsync(string? sessionId)
    {
        await _settings.SaveTranscriptionConfigAsync(CommandTemplate, RootPath);
        return RedirectToPage(new { sessionId, message = "Transcription settings saved.", kind = "success" });
    }

    public async Task<IActionResult> OnPostStartAsync(string? sessionId)
    {
        var (ok, msg) = await _transcription.StartSessionAsync(ExpectedSpeakers, HttpContext.RequestAborted);

        if (IsAjaxRequest())
        {
            var active = await _transcription.GetActiveSessionAsync(HttpContext.RequestAborted);
            return new JsonResult(new
            {
                ok,
                message = msg,
                sessionId = active?.SessionId ?? string.Empty
            }) { StatusCode = ok ? 200 : 400 };
        }

        return RedirectToPage(new { sessionId, message = msg, kind = ok ? "success" : "warning" });
    }

    public async Task<IActionResult> OnPostStopAsync(string? sessionId)
    {
        var active = await _transcription.GetActiveSessionAsync(HttpContext.RequestAborted);
        var activeSessionId = active?.SessionId ?? string.Empty;
        var (ok, msg) = await _transcription.StopSessionAsync(HttpContext.RequestAborted);

        if (IsAjaxRequest())
        {
            return new JsonResult(new
            {
                ok,
                message = msg,
                sessionId = activeSessionId
            }) { StatusCode = ok ? 200 : 400 };
        }

        return RedirectToPage(new { sessionId, message = msg, kind = ok ? "success" : "warning" });
    }

    public async Task<IActionResult> OnPostUploadChunkAsync(string sessionId, IFormFile? audioChunk)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new JsonResult(new { ok = false, message = "Missing session id." }) { StatusCode = 400 };

        if (audioChunk == null || audioChunk.Length == 0)
            return new JsonResult(new { ok = false, message = "Missing audio chunk." }) { StatusCode = 400 };

        await using var stream = audioChunk.OpenReadStream();
        var (ok, msg) = await _transcription.UploadChunkAsync(sessionId, stream, audioChunk.FileName, HttpContext.RequestAborted);

        return new JsonResult(new { ok, message = msg }) { StatusCode = ok ? 200 : 400 };
    }

    public async Task<IActionResult> OnPostAssignSpeakerAsync(string sessionId, string speaker, string? playerId)
    {
        ulong? id = null;
        string? name = null;

        if (!string.IsNullOrWhiteSpace(playerId) && ulong.TryParse(playerId, out var parsed) && parsed > 0)
        {
            id = parsed;
            var members = await _members.GetMembersAsync();
            var found = members.FirstOrDefault(m => m.Id == parsed);
            name = found.Name;
        }

        await _transcription.SaveSpeakerAssignmentAsync(sessionId, speaker, id, name, HttpContext.RequestAborted);

        if (Request.Headers.TryGetValue("X-Requested-With", out var requestedWith) &&
            string.Equals(requestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new { ok = true, message = $"Updated mapping for {speaker}." });
        }

        return RedirectToPage(new { sessionId, message = $"Updated mapping for {speaker}.", kind = "success" });
    }

    public async Task<IActionResult> OnGetLiveAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new JsonResult(new LivePayload());

        var session = await _transcription.GetSessionAsync(sessionId, HttpContext.RequestAborted);
        if (session == null)
            return new JsonResult(new LivePayload());

        var members = await _members.GetMembersAsync();
        var memberById = members.ToDictionary(m => m.Id, m => m.Name);
        var assignments = await _transcription.GetSpeakerAssignmentsAsync(sessionId, HttpContext.RequestAborted);
        var segments = await _transcription.GetSegmentsAsync(sessionId, HttpContext.RequestAborted);

        var speakers = segments
            .Select(s => s.Speaker)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new LivePayload
        {
            SessionId = session.SessionId,
            Status = session.Status,
            Error = session.Error,
            IsActive = ActiveSession?.SessionId == session.SessionId,
            Speakers = speakers.Select(s => new LiveSpeaker
            {
                Speaker = s,
                AssignedPlayerId = assignments.TryGetValue(s, out var a) ? a.PlayerId : null
            }).ToList(),
            Segments = segments.Select(s => new LiveSegment
            {
                StartSeconds = s.StartSeconds,
                EndSeconds = s.EndSeconds,
                Speaker = s.Speaker,
                DisplaySpeaker = ResolveDisplaySpeaker(s.Speaker, assignments, memberById),
                Text = s.Text
            }).ToList()
        };

        return new JsonResult(payload);
    }

    private async Task LoadAsync(string? sessionId, CancellationToken ct)
    {
        ActiveSession = await _transcription.GetActiveSessionAsync(ct);
        Sessions = await _transcription.GetRecentSessionsAsync(30, ct);

        GuildMembers = await _members.GetMembersAsync();

        var (template, rootPath) = await _settings.GetTranscriptionConfigAsync();
        CommandTemplate = string.IsNullOrWhiteSpace(template)
            ? GameplayTranscriptionService.DefaultCommandTemplate
            : template;
        RootPath = rootPath;

        ModelCacheState = await _transcription.RefreshModelCacheStateAsync(ct);
        ModelDownloadError = _transcription.GetModelDownloadError();

        SelectedSessionId = sessionId
            ?? ActiveSession?.SessionId
            ?? Sessions.FirstOrDefault()?.SessionId
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(SelectedSessionId))
            return;

        SelectedSession = await _transcription.GetSessionAsync(SelectedSessionId, ct);
        if (SelectedSession == null)
            return;

        ExpectedSpeakers = SelectedSession.ExpectedSpeakers;

        Segments = await _transcription.GetSegmentsAsync(SelectedSessionId, ct);
        SpeakerAssignments = await _transcription.GetSpeakerAssignmentsAsync(SelectedSessionId, ct);

        Speakers = Segments
            .Select(s => s.Speaker)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveDisplaySpeaker(
        string speaker,
        Dictionary<string, TranscriptSpeakerAssignment> assignments,
        Dictionary<ulong, string> memberById)
    {
        if (assignments.TryGetValue(speaker, out var assignment))
        {
            if (!string.IsNullOrWhiteSpace(assignment.PlayerName))
                return assignment.PlayerName!;

            if (assignment.PlayerId.HasValue && memberById.TryGetValue(assignment.PlayerId.Value, out var name))
                return name;
        }

        return speaker;
    }

    private bool IsAjaxRequest()
    {
        if (!Request.Headers.TryGetValue("X-Requested-With", out var requestedWith))
            return false;

        return string.Equals(requestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class LivePayload
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("isActive")] public bool IsActive { get; set; }
        [JsonPropertyName("speakers")] public List<LiveSpeaker> Speakers { get; set; } = new();
        [JsonPropertyName("segments")] public List<LiveSegment> Segments { get; set; } = new();
    }

    public sealed class LiveSpeaker
    {
        [JsonPropertyName("speaker")] public string Speaker { get; set; } = string.Empty;
        [JsonPropertyName("assignedPlayerId")] public ulong? AssignedPlayerId { get; set; }
    }

    public sealed class LiveSegment
    {
        [JsonPropertyName("startSeconds")] public double StartSeconds { get; set; }
        [JsonPropertyName("endSeconds")] public double EndSeconds { get; set; }
        [JsonPropertyName("speaker")] public string Speaker { get; set; } = string.Empty;
        [JsonPropertyName("displaySpeaker")] public string DisplaySpeaker { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}
