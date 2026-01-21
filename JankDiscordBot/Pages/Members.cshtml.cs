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
                    BirthdayMonth = profile?.BirthdayMonth,
                    BirthdayDay = profile?.BirthdayDay
                };
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync(string userId, string? characterName, string? notes, int? birthdayMonth, int? birthdayDay)
    {
        if (!ulong.TryParse(userId, out var id) || id == 0) return RedirectToPage();

        var profile = await _repo.GetMemberProfileAsync(id) ?? new MemberProfile { UserId = id };
        profile.CharacterName = string.IsNullOrWhiteSpace(characterName) ? null : characterName.Trim();
        profile.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        if (birthdayMonth.HasValue && birthdayDay.HasValue)
        {
            profile.BirthdayMonth = Math.Clamp(birthdayMonth.Value, 1, 12);
            profile.BirthdayDay = Math.Clamp(birthdayDay.Value, 1, 31);
        }
        else
        {
            profile.BirthdayMonth = null;
            profile.BirthdayDay = null;
            profile.BirthdayLastSentYear = null;
        }

        await _repo.UpsertMemberProfileAsync(profile);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync(string userId)
    {
        if (!ulong.TryParse(userId, out var id) || id == 0) return RedirectToPage();
        await _repo.DeleteMemberProfileAsync(id);
        return RedirectToPage();
    }

    public sealed class MemberRow
    {
        public ulong UserId { get; set; }
        public string Name { get; set; } = "";
        public string? CharacterName { get; set; }
        public string? Notes { get; set; }
        public int? BirthdayMonth { get; set; }
        public int? BirthdayDay { get; set; }
    }
}
