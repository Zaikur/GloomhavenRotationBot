using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GloomhavenRotationBot.Data;

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

}
