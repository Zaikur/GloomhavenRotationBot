using System.Linq;
using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class MembersModel : PageModel
{
    private readonly BotRepository _repo;
    private readonly GuildMemberDirectory _members;

    public MembersModel(BotRepository repo, GuildMemberDirectory members)
    {
        _repo = repo;
        _members = members;
    }

    public List<MemberRow> Rows { get; private set; } = new();
    public string? Warning { get; private set; }

    // Binding for bulk save
    [BindProperty]
    public List<MemberUpdate>? MemberUpdates { get; set; }

    public async Task OnGetAsync()
    {
        var guildMembers = await _members.GetMembersAsync();
        var profiles = (await _repo.GetAllMemberProfilesAsync()).ToDictionary(p => p.UserId, p => p);

        if (guildMembers.Count == 0)
            Warning = "No guild members loaded. Make sure the bot is connected and has the Guild Members intent.";

        Rows = guildMembers
            .Select(m =>
            {
                profiles.TryGetValue(m.Id, out var profile);
                return new MemberRow
                {
                    UserId = m.Id,
                    Name = m.Name,
                    CharacterName = profile?.CharacterName ?? "",
                    Notes = profile?.Notes ?? "",
                    AiNotes = profile?.AiNotes ?? "",
                    BirthdayMonth = profile?.BirthdayMonth,
                    BirthdayDay = profile?.BirthdayDay,
                    Latitude = profile?.Latitude,
                    Longitude = profile?.Longitude,
                    LocationName = profile?.LocationName ?? ""
                };
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (MemberUpdates == null || MemberUpdates.Count == 0)
            return RedirectToPage();

        foreach (var update in MemberUpdates)
        {
            if (!ulong.TryParse(update.UserId, out var id) || id == 0)
                continue;

            var profile = await _repo.GetMemberProfileAsync(id) ?? new MemberProfile { UserId = id };
            profile.CharacterName = string.IsNullOrWhiteSpace(update.CharacterName) ? null : update.CharacterName.Trim();
            profile.Notes = string.IsNullOrWhiteSpace(update.Notes) ? null : update.Notes.Trim();
            profile.LocationName = string.IsNullOrWhiteSpace(update.LocationName) ? null : update.LocationName.Trim();

            if (!string.IsNullOrWhiteSpace(update.AiNotes))
            {
                var trimmed = update.AiNotes.Trim();
                profile.AiNotes = trimmed.Length > MemberProfile.MaxAiNotesLength
                    ? trimmed.Substring(0, MemberProfile.MaxAiNotesLength)
                    : trimmed;
            }
            else
            {
                profile.AiNotes = null;
            }

            if (update.BirthdayMonth.HasValue && update.BirthdayDay.HasValue)
            {
                profile.BirthdayMonth = Math.Clamp(update.BirthdayMonth.Value, 1, 12);
                profile.BirthdayDay = Math.Clamp(update.BirthdayDay.Value, 1, 31);
            }
            else
            {
                profile.BirthdayMonth = null;
                profile.BirthdayDay = null;
                profile.BirthdayLastSentYear = null;
            }

            profile.Latitude = update.Latitude;
            profile.Longitude = update.Longitude;

            await _repo.UpsertMemberProfileAsync(profile);
        }

        return RedirectToPage();
    }

    public sealed class MemberRow
    {
        public ulong UserId { get; set; }
        public string Name { get; set; } = "";
        public string? CharacterName { get; set; }
        public string? Notes { get; set; }
        public string? AiNotes { get; set; }
        public int? BirthdayMonth { get; set; }
        public int? BirthdayDay { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? LocationName { get; set; }
    }

    public sealed class MemberUpdate
    {
        public string UserId { get; set; } = "";
        public string? CharacterName { get; set; }
        public string? Notes { get; set; }
        public string? AiNotes { get; set; }
        public int? BirthdayMonth { get; set; }
        public int? BirthdayDay { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? LocationName { get; set; }
    }
}
