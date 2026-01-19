using GloomhavenRotationBot.Data;
using GloomhavenRotationBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class RostersModel : PageModel
{
    private readonly BotRepository _repo;
    private readonly GuildMemberDirectory _members;

    public RostersModel(BotRepository repo, GuildMemberDirectory members)
    {
        _repo = repo;
        _members = members;
    }

    public RotationState Dm { get; private set; } = new();
    public RotationState Food { get; private set; } = new();

    public List<(ulong Id, string Name)> GuildMembers { get; private set; } = new();
    public string? Warning { get; private set; }
    public Dictionary<ulong, (int Month, int Day)> Birthdays { get; private set; } = new();

    private Dictionary<ulong, string> _nameById = new();
    public string NameFor(ulong id) => _nameById.TryGetValue(id, out var n) ? n : $"Unknown ({id})";

    public async Task OnGetAsync()
    {
        Dm = await _repo.GetRotationAsync(RotationRole.DM);
        Food = await _repo.GetRotationAsync(RotationRole.Food);

        GuildMembers = await _members.GetMembersAsync();
        _nameById = GuildMembers.ToDictionary(x => x.Id, x => x.Name);

        var b = await _repo.GetAllBirthdaysAsync();
        Birthdays = b.ToDictionary(x => x.UserId, x => (x.Month, x.Day));

        if (GuildMembers.Count == 0)
            Warning = "No guild members loaded. Make sure the bot is connected, GuildId is correct, and Server Members Intent is enabled.";
    }

    public async Task<IActionResult> OnPostAddAsync(string role, string userId)
    {
        if (!ulong.TryParse(userId, out var id) || id == 0)
            return RedirectToPage();

        var r = ParseRole(role);
        var state = await _repo.GetRotationAsync(r);

        if (!state.Members.Contains(id))
            state.Members.Add(id);

        await _repo.SaveRotationAsync(r, state);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetBirthdayAsync(string userId, int? Month, int? Day)
    {
        if (!ulong.TryParse(userId, out var id) || id == 0) return RedirectToPage();
        if (Month == null || Day == null) return RedirectToPage();

        // basic validation: clamp day for month
        var month = Math.Clamp(Month.Value, 1, 12);
        var day = Math.Clamp(Day.Value, 1, 31);

        await _repo.UpsertBirthdayAsync(id, month, day);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearBirthdayAsync(string userId)
    {
        if (!ulong.TryParse(userId, out var id) || id == 0) return RedirectToPage();
        await _repo.DeleteBirthdayAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string role, int index)
    {
        var r = ParseRole(role);
        var state = await _repo.GetRotationAsync(r);

        if (index >= 0 && index < state.Members.Count)
            state.Members.RemoveAt(index);

        if (state.Index >= state.Members.Count) state.Index = 0;

        await _repo.SaveRotationAsync(r, state);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpAsync(string role, int index)
    {
        var r = ParseRole(role);
        var state = await _repo.GetRotationAsync(r);

        if (index > 0 && index < state.Members.Count)
        {
            (state.Members[index - 1], state.Members[index]) = (state.Members[index], state.Members[index - 1]);
            if (state.Index == index) state.Index = index - 1;
            else if (state.Index == index - 1) state.Index = index;
        }

        await _repo.SaveRotationAsync(r, state);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDownAsync(string role, int index)
    {
        var r = ParseRole(role);
        var state = await _repo.GetRotationAsync(r);

        if (index >= 0 && index < state.Members.Count - 1)
        {
            (state.Members[index], state.Members[index + 1]) = (state.Members[index + 1], state.Members[index]);
            if (state.Index == index) state.Index = index + 1;
            else if (state.Index == index + 1) state.Index = index;
        }

        await _repo.SaveRotationAsync(r, state);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetCurrentAsync(string role, int index)
    {
        var r = ParseRole(role);
        var state = await _repo.GetRotationAsync(r);

        if (index >= 0 && index < state.Members.Count)
            state.Index = index;

        await _repo.SaveRotationAsync(r, state);
        return RedirectToPage();
    }

    private static RotationRole ParseRole(string role)
        => role?.Trim().ToLowerInvariant() == "food" ? RotationRole.Food : RotationRole.DM;
}
