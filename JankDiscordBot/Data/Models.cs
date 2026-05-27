namespace GloomhavenRotationBot.Data;

public enum RotationRole
{
    DM = 0,
    Food = 1
}

public sealed class RotationState
{
    public List<ulong> Members { get; set; } = new();
    public int Index { get; set; } = 0;
    public HashSet<ulong> AbsentMembers { get; set; } = new();

    public bool IsAbsent(ulong userId)
        => AbsentMembers?.Contains(userId) == true;

    public int? GetCurrentAvailableIndex()
    {
        if (Members.Count == 0)
            return null;

        var absent = AbsentMembers ?? new HashSet<ulong>();
        var start = NormalizeIndex(Index, Members.Count);

        for (var i = 0; i < Members.Count; i++)
        {
            var idx = (start + i) % Members.Count;
            if (!absent.Contains(Members[idx]))
                return idx;
        }

        return null;
    }

    public ulong? GetCurrentAvailableMember()
    {
        var idx = GetCurrentAvailableIndex();
        return idx is null ? null : Members[idx.Value];
    }

    public int? GetOffsetAvailableIndex(int offset)
    {
        if (Members.Count == 0)
            return null;

        var absent = AbsentMembers ?? new HashSet<ulong>();
        var available = Enumerable.Range(0, Members.Count)
            .Where(i => !absent.Contains(Members[i]))
            .ToList();

        if (available.Count == 0)
            return null;

        var current = GetCurrentAvailableIndex();
        if (current is null)
            return null;

        var currentPos = available.IndexOf(current.Value);
        if (currentPos < 0)
            currentPos = 0;

        var targetPos = Mod(currentPos + offset, available.Count);
        return available[targetPos];
    }

    public bool TryAdvanceToNextAvailable()
    {
        var next = GetOffsetAvailableIndex(1);
        if (next is null)
            return false;

        Index = next.Value;
        return true;
    }

    private static int NormalizeIndex(int index, int count)
    {
        if (count <= 0)
            return 0;

        var m = index % count;
        return m < 0 ? m + count : m;
    }

    private static int Mod(int value, int modulus)
    {
        if (modulus <= 0)
            return 0;

        var m = value % modulus;
        return m < 0 ? m + modulus : m;
    }
}

public sealed class MeetingOverride
{
    public DateOnly Date { get; set; }
    public bool IsMeeting { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
