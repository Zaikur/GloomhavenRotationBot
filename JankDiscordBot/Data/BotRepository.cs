using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GloomhavenRotationBot.Data;

public sealed record SessionOverrideRow(
    DateOnly OriginalDateLocal,
    bool IsCancelled,
    DateTime? MovedToLocal,
    string? Note);

public sealed record SessionMarkersRow(
    string OccurrenceId,
    bool AnnouncedMorning,
    DateTime? AnnouncedUtc,
    bool Advanced,
    DateTime? AdvancedUtc);

public sealed class BotRepository
{
    private readonly string _dbPath;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public BotRepository(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _dbPath = config["Data:DbPath"] ?? "data/app.db";
        EnsureCreated();
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        return new SqliteConnection($"Data Source={_dbPath}");
    }

    private void EnsureCreated()
    {
        using var con = Open();
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Rotations (
              Role TEXT PRIMARY KEY,
              Json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SessionOverrides (
              OriginalDateLocal TEXT PRIMARY KEY,     
              IsCancelled INTEGER NOT NULL DEFAULT 0,
              MovedToLocal TEXT NULL,
              Note TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS SessionMarkers (
              OccurrenceId TEXT PRIMARY KEY,
              AnnouncedMorning INTEGER NOT NULL DEFAULT 0,
              AnnouncedUtc TEXT NULL,
              Advanced INTEGER NOT NULL DEFAULT 0,
              AdvancedUtc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS AppSettings (
              Key TEXT PRIMARY KEY,
              Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MeetingOverrides (
              Date TEXT PRIMARY KEY,
              IsMeeting INTEGER NOT NULL,
              Note TEXT NULL,
              UpdatedUtc TEXT NOT NULL
            );
            ";
        cmd.ExecuteNonQuery();

        // Seed empty rotation rows (dm/cook) if missing
        foreach (var role in Enum.GetNames(typeof(RotationRole)))
        {
            using var check = con.CreateCommand();
            check.CommandText = "SELECT COUNT(1) FROM Rotations WHERE Role = @r";
            check.Parameters.AddWithValue("@r", role);
            var exists = Convert.ToInt32(check.ExecuteScalar()) > 0;
            if (!exists)
            {
                var empty = new RotationState();
                using var ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO Rotations(Role, Json) VALUES(@r, @j)";
                ins.Parameters.AddWithValue("@r", role);
                ins.Parameters.AddWithValue("@j", JsonSerializer.Serialize(empty, JsonOpts));
                ins.ExecuteNonQuery();
            }
        }
    }

    public async Task<RotationState> GetRotationAsync(RotationRole role)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Json FROM Rotations WHERE Role = @r";
        cmd.Parameters.AddWithValue("@r", role.ToString());

        var json = (string?)await cmd.ExecuteScalarAsync();
        if (string.IsNullOrWhiteSpace(json))
            return new RotationState();

        return JsonSerializer.Deserialize<RotationState>(json, JsonOpts) ?? new RotationState();
    }

    public async Task SaveRotationAsync(RotationRole role, RotationState state)
    {
        state.Index = NormalizeIndex(state.Index, state.Members.Count);

        var json = JsonSerializer.Serialize(state, JsonOpts);

        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Rotations SET Json = @j WHERE Role = @r";
        cmd.Parameters.AddWithValue("@r", role.ToString());
        cmd.Parameters.AddWithValue("@j", json);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<MeetingOverride?> GetOverrideAsync(DateOnly date)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Date, IsMeeting, Note, UpdatedUtc FROM MeetingOverrides WHERE Date = @d";
        cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));

        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync()) return null;

        return new MeetingOverride
        {
            Date = DateOnly.Parse(rdr.GetString(0)),
            IsMeeting = rdr.GetInt32(1) != 0,
            Note = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            UpdatedUtc = DateTime.Parse(rdr.GetString(3)).ToUniversalTime()
        };
    }

    public async Task UpsertOverrideAsync(DateOnly date, bool isMeeting, string? note)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO MeetingOverrides(Date, IsMeeting, Note, UpdatedUtc)
            VALUES(@d, @m, @n, @u)
            ON CONFLICT(Date) DO UPDATE SET
              IsMeeting = excluded.IsMeeting,
              Note = excluded.Note,
              UpdatedUtc = excluded.UpdatedUtc;
            ";
        cmd.Parameters.AddWithValue("@d", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@m", isMeeting ? 1 : 0);
        cmd.Parameters.AddWithValue("@n", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@u", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    private static int NormalizeIndex(int index, int count)
    {
        if (count <= 0) return 0;
        var m = index % count;
        if (m < 0) m += count;
        return m;
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = @k";
        cmd.Parameters.AddWithValue("@k", key);

        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task UpsertSettingAsync(string key, string value)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AppSettings(Key, Value) VALUES(@k, @v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            ";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<SessionOverrideRow>> GetOverridesInRangeAsync(DateOnly startInclusive, DateOnly endInclusive)
    {
        await using var con = Open();
        await con.OpenAsync();

        // We store OriginalDateLocal as YYYY-MM-DD text so BETWEEN works lexicographically.
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT OriginalDateLocal, IsCancelled, MovedToLocal, Note
            FROM SessionOverrides
            WHERE OriginalDateLocal BETWEEN @s AND @e";
        cmd.Parameters.AddWithValue("@s", startInclusive.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@e", endInclusive.ToString("yyyy-MM-dd"));

        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<SessionOverrideRow>();

        while (await r.ReadAsync())
        {
            var d = DateOnly.Parse(r.GetString(0));
            var cancelled = r.GetInt32(1) == 1;

            DateTime? moved = null;
            if (!r.IsDBNull(2))
                moved = DateTime.Parse(r.GetString(2)); // stored local

            var note = r.IsDBNull(3) ? null : r.GetString(3);

            list.Add(new SessionOverrideRow(d, cancelled, moved, note));
        }

        return list;
    }

    public async Task DeleteSessionOverrideAsync(DateOnly originalDate)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"DELETE FROM SessionOverrides WHERE OriginalDateLocal = @d";
        cmd.Parameters.AddWithValue("@d", originalDate.ToString("yyyy-MM-dd"));

        await cmd.ExecuteNonQueryAsync();
    }

    private static string DateKey(DateOnly d) => d.ToString("yyyy-MM-dd");

    public async Task<SessionOverrideRow?> GetSessionOverrideAsync(DateOnly originalDate)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT OriginalDateLocal, IsCancelled, MovedToLocal, Note
                            FROM SessionOverrides
                            WHERE OriginalDateLocal = @d";
        cmd.Parameters.AddWithValue("@d", DateKey(originalDate));

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var d = DateOnly.Parse(r.GetString(0));
        var cancelled = r.GetInt32(1) == 1;

        DateTime? moved = null;
        if (!r.IsDBNull(2))
            moved = DateTime.Parse(r.GetString(2)); // local time stored as ISO

        var note = r.IsDBNull(3) ? null : r.GetString(3);

        return new SessionOverrideRow(d, cancelled, moved, note);
    }

    public async Task<List<SessionOverrideRow>> GetOverridesMovedToDateAsync(DateOnly targetDate)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT OriginalDateLocal, IsCancelled, MovedToLocal, Note
                            FROM SessionOverrides
                            WHERE MovedToLocal IS NOT NULL";
        await using var r = await cmd.ExecuteReaderAsync();

        var list = new List<SessionOverrideRow>();
        while (await r.ReadAsync())
        {
            var d = DateOnly.Parse(r.GetString(0));
            var cancelled = r.GetInt32(1) == 1;
            var moved = DateTime.Parse(r.GetString(2));
            if (DateOnly.FromDateTime(moved) != targetDate) continue;

            var note = r.IsDBNull(3) ? null : r.GetString(3);
            list.Add(new SessionOverrideRow(d, cancelled, moved, note));
        }

        return list;
    }

    public async Task UpsertSessionOverrideAsync(SessionOverrideRow row)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SessionOverrides (OriginalDateLocal, IsCancelled, MovedToLocal, Note)
            VALUES (@d, @c, @m, @n)
            ON CONFLICT(OriginalDateLocal) DO UPDATE SET
              IsCancelled = excluded.IsCancelled,
              MovedToLocal = excluded.MovedToLocal,
              Note = excluded.Note;";

        cmd.Parameters.AddWithValue("@d", DateKey(row.OriginalDateLocal));
        cmd.Parameters.AddWithValue("@c", row.IsCancelled ? 1 : 0);
        cmd.Parameters.AddWithValue("@m", row.MovedToLocal is null ? DBNull.Value : row.MovedToLocal.Value.ToString("s"));
        cmd.Parameters.AddWithValue("@n", row.Note ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SessionMarkersRow?> GetMarkersAsync(string occurrenceId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT OccurrenceId, AnnouncedMorning, AnnouncedUtc, Advanced, AdvancedUtc
                            FROM SessionMarkers
                            WHERE OccurrenceId = @id";
        cmd.Parameters.AddWithValue("@id", occurrenceId);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        DateTime? announcedUtc = r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)).ToUniversalTime();
        DateTime? advancedUtc = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4)).ToUniversalTime();

        return new SessionMarkersRow(
            r.GetString(0),
            r.GetInt32(1) == 1,
            announcedUtc,
            r.GetInt32(3) == 1,
            advancedUtc
        );
    }

    public async Task SetAnnouncedAsync(string occurrenceId, DateTime utcNow)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SessionMarkers (OccurrenceId, AnnouncedMorning, AnnouncedUtc, Advanced, AdvancedUtc)
            VALUES (@id, 1, @t, 0, NULL)
            ON CONFLICT(OccurrenceId) DO UPDATE SET
              AnnouncedMorning = 1,
              AnnouncedUtc = excluded.AnnouncedUtc;";
        cmd.Parameters.AddWithValue("@id", occurrenceId);
        cmd.Parameters.AddWithValue("@t", utcNow.ToString("s"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetAdvancedAsync(string occurrenceId, DateTime utcNow)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SessionMarkers (OccurrenceId, AnnouncedMorning, AnnouncedUtc, Advanced, AdvancedUtc)
            VALUES (@id, 0, NULL, 1, @t)
            ON CONFLICT(OccurrenceId) DO UPDATE SET
              Advanced = 1,
              AdvancedUtc = excluded.AdvancedUtc;";
        cmd.Parameters.AddWithValue("@id", occurrenceId);
        cmd.Parameters.AddWithValue("@t", utcNow.ToString("s"));

        await cmd.ExecuteNonQueryAsync();
    }
}
