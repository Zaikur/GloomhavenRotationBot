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

            CREATE TABLE IF NOT EXISTS Birthdays (
                UserId TEXT PRIMARY KEY,
                Month INTEGER NOT NULL,
                Day INTEGER NOT NULL,
                LastSentYear INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS MemberProfiles (
                UserId TEXT PRIMARY KEY,
                CharacterName TEXT NULL,
                Notes TEXT NULL,
                BirthdayMonth INTEGER NULL,
                BirthdayDay INTEGER NULL,
                BirthdayLastSentYear INTEGER NULL,
                Latitude REAL NULL,
                Longitude REAL NULL,
                LocationName TEXT NULL,
                AiNotes TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ChatMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                MessageText TEXT NOT NULL,
                TimestampUtc TEXT NOT NULL,
                IsBot INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_chat_user_time ON ChatMessages(UserId, TimestampUtc DESC);

            CREATE TABLE IF NOT EXISTS Surveys (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Description TEXT NULL,
                CreatedByUserId TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                CloseAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Open',
                PostChannelId TEXT NULL,
                ResultsMessageId TEXT NULL,
                HotTakes TEXT NULL,
                InvitedCount INTEGER NOT NULL DEFAULT 0,
                RespondedCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS SurveyQuestions (
                Id TEXT PRIMARY KEY,
                SurveyId TEXT NOT NULL,
                Order_Index INTEGER NOT NULL,
                Text TEXT NOT NULL,
                FOREIGN KEY(SurveyId) REFERENCES Surveys(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_survey_questions ON SurveyQuestions(SurveyId);

            CREATE TABLE IF NOT EXISTS SurveyOptions (
                Id TEXT PRIMARY KEY,
                QuestionId TEXT NOT NULL,
                Order_Index INTEGER NOT NULL,
                Text TEXT NOT NULL,
                ResponseCount INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(QuestionId) REFERENCES SurveyQuestions(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_question_options ON SurveyOptions(QuestionId);

            CREATE TABLE IF NOT EXISTS SurveyResponses (
                Id TEXT PRIMARY KEY,
                SurveyId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                QuestionId TEXT NOT NULL,
                SelectedOptionId TEXT NOT NULL,
                SubmittedUtc TEXT NOT NULL,
                FOREIGN KEY(SurveyId) REFERENCES Surveys(Id) ON DELETE CASCADE,
                FOREIGN KEY(QuestionId) REFERENCES SurveyQuestions(Id) ON DELETE CASCADE,
                FOREIGN KEY(SelectedOptionId) REFERENCES SurveyOptions(Id) ON DELETE CASCADE,
                UNIQUE(SurveyId, UserId, QuestionId)
            );

            CREATE INDEX IF NOT EXISTS idx_responses_survey_user ON SurveyResponses(SurveyId, UserId);

            CREATE TABLE IF NOT EXISTS SurveyFeedback (
                Id TEXT PRIMARY KEY,
                SurveyId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                FeedbackText TEXT NOT NULL,
                SubmittedUtc TEXT NOT NULL,
                FOREIGN KEY(SurveyId) REFERENCES Surveys(Id) ON DELETE CASCADE,
                UNIQUE(SurveyId, UserId)
            );

            CREATE INDEX IF NOT EXISTS idx_feedback_survey ON SurveyFeedback(SurveyId);
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

        // Migrate birthdays into member profiles if profiles are empty
        using (var checkProfiles = con.CreateCommand())
        {
            checkProfiles.CommandText = "SELECT COUNT(1) FROM MemberProfiles";
            var profileCount = Convert.ToInt32(checkProfiles.ExecuteScalar());

            if (profileCount == 0)
            {
                using var checkBirthdays = con.CreateCommand();
                checkBirthdays.CommandText = "SELECT COUNT(1) FROM Birthdays";
                var birthdayCount = Convert.ToInt32(checkBirthdays.ExecuteScalar());

                if (birthdayCount > 0)
                {
                    using var migrate = con.CreateCommand();
                    migrate.CommandText = @"
                        INSERT INTO MemberProfiles (UserId, BirthdayMonth, BirthdayDay, BirthdayLastSentYear)
                        SELECT UserId, Month, Day, LastSentYear FROM Birthdays;
                    ";
                    migrate.ExecuteNonQuery();
                }
            }
        }

        MigrateAddColumnIfMissing(con, "MemberProfiles", "Latitude", "REAL NULL");
        MigrateAddColumnIfMissing(con, "MemberProfiles", "Longitude", "REAL NULL");
        MigrateAddColumnIfMissing(con, "MemberProfiles", "LocationName", "TEXT NULL");
        MigrateAddColumnIfMissing(con, "MemberProfiles", "AiNotes", "TEXT NULL");
    }

    private static void MigrateAddColumnIfMissing(SqliteConnection con, string tableName, string columnName, string columnDef)
    {
        // Check if column exists by querying PRAGMA table_info
        using var check = con.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = check.ExecuteReader();
        bool exists = false;
        while (reader.Read())
        {
            var name = reader.GetString(1); // column name is at index 1
            if (name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
        reader.Close();

        if (!exists)
        {
            using var alter = con.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef}";
            alter.ExecuteNonQuery();
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

    // Member Profiles / Birthdays
    public async Task<List<MemberProfile>> GetAllMemberProfilesAsync()
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT UserId, CharacterName, Notes, BirthdayMonth, BirthdayDay, BirthdayLastSentYear, Latitude, Longitude, LocationName, AiNotes FROM MemberProfiles";

        var list = new List<MemberProfile>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new MemberProfile
            {
                UserId = ulong.Parse(r.GetString(0)),
                CharacterName = r.IsDBNull(1) ? null : r.GetString(1),
                Notes = r.IsDBNull(2) ? null : r.GetString(2),
                BirthdayMonth = r.IsDBNull(3) ? null : r.GetInt32(3),
                BirthdayDay = r.IsDBNull(4) ? null : r.GetInt32(4),
                BirthdayLastSentYear = r.IsDBNull(5) ? null : r.GetInt32(5),
                Latitude = r.IsDBNull(6) ? null : r.GetDouble(6),
                Longitude = r.IsDBNull(7) ? null : r.GetDouble(7),
                LocationName = r.IsDBNull(8) ? null : r.GetString(8),
                AiNotes = r.IsDBNull(9) ? null : r.GetString(9)
            });
        }

        return list;
    }

    public async Task<MemberProfile?> GetMemberProfileAsync(ulong userId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT UserId, CharacterName, Notes, BirthdayMonth, BirthdayDay, BirthdayLastSentYear, Latitude, Longitude, LocationName, AiNotes FROM MemberProfiles WHERE UserId = @id";
        cmd.Parameters.AddWithValue("@id", userId.ToString());

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new MemberProfile
        {
            UserId = ulong.Parse(r.GetString(0)),
            CharacterName = r.IsDBNull(1) ? null : r.GetString(1),
            Notes = r.IsDBNull(2) ? null : r.GetString(2),
            BirthdayMonth = r.IsDBNull(3) ? null : r.GetInt32(3),
            BirthdayDay = r.IsDBNull(4) ? null : r.GetInt32(4),
            BirthdayLastSentYear = r.IsDBNull(5) ? null : r.GetInt32(5),
            Latitude = r.IsDBNull(6) ? null : r.GetDouble(6),
            Longitude = r.IsDBNull(7) ? null : r.GetDouble(7),
            LocationName = r.IsDBNull(8) ? null : r.GetString(8),
            AiNotes = r.IsDBNull(9) ? null : r.GetString(9)
        };
    }

    public async Task UpsertMemberProfileAsync(MemberProfile profile, bool syncLegacyBirthdays = true)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO MemberProfiles (UserId, CharacterName, Notes, BirthdayMonth, BirthdayDay, BirthdayLastSentYear, Latitude, Longitude, LocationName, AiNotes)
            VALUES (@id, @c, @n, @bm, @bd, @bly, @lat, @lon, @loc, @ai)
            ON CONFLICT(UserId) DO UPDATE SET
              CharacterName = excluded.CharacterName,
              Notes = excluded.Notes,
              BirthdayMonth = excluded.BirthdayMonth,
              BirthdayDay = excluded.BirthdayDay,
              BirthdayLastSentYear = excluded.BirthdayLastSentYear,
              Latitude = excluded.Latitude,
              Longitude = excluded.Longitude,
              LocationName = excluded.LocationName,
              AiNotes = excluded.AiNotes;";
        cmd.Parameters.AddWithValue("@id", profile.UserId.ToString());
        cmd.Parameters.AddWithValue("@c", (object?)profile.CharacterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@n", (object?)profile.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bm", (object?)profile.BirthdayMonth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bd", (object?)profile.BirthdayDay ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bly", (object?)profile.BirthdayLastSentYear ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lat", (object?)profile.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lon", (object?)profile.Longitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@loc", (object?)profile.LocationName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ai", (object?)profile.AiNotes ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();

        if (syncLegacyBirthdays)
        {
            if (profile.BirthdayMonth.HasValue && profile.BirthdayDay.HasValue)
            {
                await UpsertBirthdayLegacyAsync(profile.UserId, profile.BirthdayMonth.Value, profile.BirthdayDay.Value);
                if (profile.BirthdayLastSentYear.HasValue)
                    await SetBirthdaySentYearLegacyAsync(profile.UserId, profile.BirthdayLastSentYear.Value);
            }
            else
            {
                await DeleteBirthdayLegacyAsync(profile.UserId);
            }
        }
    }

    public async Task DeleteMemberProfileAsync(ulong userId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM MemberProfiles WHERE UserId = @id";
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync();

        await DeleteBirthdayAsync(userId);
    }

    // Legacy birthday helpers (kept for compatibility with existing callers)
    public async Task UpsertBirthdayAsync(ulong userId, int month, int day)
    {
        await UpsertBirthdayLegacyAsync(userId, month, day);

        // mirror into member profiles without re-syncing legacy tables
        var existing = await GetMemberProfileAsync(userId) ?? new MemberProfile { UserId = userId };
        existing.BirthdayMonth = month;
        existing.BirthdayDay = day;
        await UpsertMemberProfileAsync(existing, syncLegacyBirthdays: false);
    }

    public async Task DeleteBirthdayAsync(ulong userId)
    {
        await DeleteBirthdayLegacyAsync(userId);

        var existing = await GetMemberProfileAsync(userId);
        if (existing != null)
        {
            existing.BirthdayMonth = null;
            existing.BirthdayDay = null;
            existing.BirthdayLastSentYear = null;
            await UpsertMemberProfileAsync(existing, syncLegacyBirthdays: false);
        }
    }

    public async Task<List<(ulong UserId, int Month, int Day, int? LastSentYear)>> GetAllBirthdaysAsync()
    {
        var profiles = await GetAllMemberProfilesAsync();
        return profiles
            .Where(p => p.BirthdayMonth.HasValue && p.BirthdayDay.HasValue)
            .Select(p => (p.UserId, p.BirthdayMonth!.Value, p.BirthdayDay!.Value, p.BirthdayLastSentYear))
            .ToList();
    }

    public async Task SetBirthdaySentYearAsync(ulong userId, int year)
    {
        // Update member profile
        var profile = await GetMemberProfileAsync(userId) ?? new MemberProfile { UserId = userId };
        profile.BirthdayLastSentYear = year;
        await UpsertMemberProfileAsync(profile);
        await SetBirthdaySentYearLegacyAsync(userId, year);
    }

    private async Task SetBirthdaySentYearLegacyAsync(ulong userId, int year)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Birthdays SET LastSentYear = @y WHERE UserId = @id";
        cmd.Parameters.AddWithValue("@y", year);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpsertBirthdayLegacyAsync(ulong userId, int month, int day)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Birthdays (UserId, Month, Day, LastSentYear)
            VALUES (@id, @m, @d, NULL)
            ON CONFLICT(UserId) DO UPDATE SET
              Month = excluded.Month,
              Day = excluded.Day;";
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        cmd.Parameters.AddWithValue("@m", month);
        cmd.Parameters.AddWithValue("@d", day);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteBirthdayLegacyAsync(ulong userId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Birthdays WHERE UserId = @id";
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync();
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

    // Chat message history
    public async Task SaveChatMessageAsync(ulong userId, string messageText, bool isBot)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ChatMessages (UserId, MessageText, TimestampUtc, IsBot)
            VALUES (@uid, @msg, @ts, @bot);";
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        cmd.Parameters.AddWithValue("@msg", messageText);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@bot", isBot ? 1 : 0);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Gets recent chat messages for a user.
    /// Returns messages from today, or all messages within the specified timeframe window
    /// if there's an active conversation (messages within conversationWindowMinutes).
    /// Caps at maxMessages total.
    /// </summary>
    public async Task<List<ChatMessage>> GetRecentChatMessagesAsync(ulong userId, int conversationWindowMinutes = 30, int maxMessages = 20)
    {
        await using var con = Open();
        await con.OpenAsync();

        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var conversationCutoff = now.AddMinutes(-conversationWindowMinutes);

        await using var cmd = con.CreateCommand();
        // Get messages from today OR within the conversation window, ordered newest first, then reverse
        cmd.CommandText = @"
            SELECT Id, UserId, MessageText, TimestampUtc, IsBot
            FROM ChatMessages
            WHERE UserId = @uid
              AND (TimestampUtc >= @today OR TimestampUtc >= @convo)
            ORDER BY TimestampUtc DESC
            LIMIT @max;";
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        cmd.Parameters.AddWithValue("@today", todayStart.ToString("O"));
        cmd.Parameters.AddWithValue("@convo", conversationCutoff.ToString("O"));
        cmd.Parameters.AddWithValue("@max", maxMessages);

        var list = new List<ChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ChatMessage
            {
                Id = r.GetInt64(0),
                UserId = ulong.Parse(r.GetString(1)),
                MessageText = r.GetString(2),
                TimestampUtc = DateTime.Parse(r.GetString(3)).ToUniversalTime(),
                IsBot = r.GetInt32(4) == 1
            });
        }

        // Reverse to chronological order (oldest first)
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Updates the AI notes for a user. Enforces character limit.
    /// </summary>
    public async Task UpdateAiNotesAsync(ulong userId, string? aiNotes)
    {
        if (aiNotes != null && aiNotes.Length > MemberProfile.MaxAiNotesLength)
        {
            aiNotes = aiNotes.Substring(0, MemberProfile.MaxAiNotesLength);
        }

        var profile = await GetMemberProfileAsync(userId) ?? new MemberProfile { UserId = userId };
        profile.AiNotes = aiNotes;
        await UpsertMemberProfileAsync(profile, syncLegacyBirthdays: false);
    }

    /// <summary>
    /// Updates the location for a user.
    /// </summary>
    public async Task UpdateUserLocationAsync(ulong userId, double? latitude, double? longitude, string? locationName)
    {
        var profile = await GetMemberProfileAsync(userId) ?? new MemberProfile { UserId = userId };
        profile.Latitude = latitude;
        profile.Longitude = longitude;
        profile.LocationName = locationName;
        await UpsertMemberProfileAsync(profile, syncLegacyBirthdays: false);
    }

    // Survey-related methods
    public async Task<Survey> CreateSurveyAsync(Survey survey)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Surveys (Id, Title, Description, CreatedByUserId, CreatedUtc, CloseAtUtc, Status, PostChannelId, ResultsMessageId, HotTakes, InvitedCount, RespondedCount)
            VALUES (@id, @t, @d, @cby, @cc, @cl, @s, @pch, @rmsg, @ht, @inv, @resp)";
        cmd.Parameters.AddWithValue("@id", survey.Id);
        cmd.Parameters.AddWithValue("@t", survey.Title);
        cmd.Parameters.AddWithValue("@d", (object?)survey.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cby", survey.CreatedByUserId.ToString());
        cmd.Parameters.AddWithValue("@cc", survey.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@cl", survey.CloseAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@s", survey.Status);
        cmd.Parameters.AddWithValue("@pch", (object?)(survey.PostChannelId.HasValue ? survey.PostChannelId.Value.ToString() : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rmsg", (object?)(survey.ResultsMessageId.HasValue ? survey.ResultsMessageId.Value.ToString() : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ht", (object?)survey.HotTakes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inv", survey.InvitedCount);
        cmd.Parameters.AddWithValue("@resp", survey.RespondedCount);

        await cmd.ExecuteNonQueryAsync();
        return survey;
    }

    public async Task<Survey?> GetSurveyAsync(string surveyId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, Description, CreatedByUserId, CreatedUtc, CloseAtUtc, Status, PostChannelId, ResultsMessageId, HotTakes, InvitedCount, RespondedCount
            FROM Surveys WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", surveyId);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new Survey
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Description = r.IsDBNull(2) ? null : r.GetString(2),
            CreatedByUserId = ulong.Parse(r.GetString(3)),
            CreatedUtc = DateTime.Parse(r.GetString(4)).ToUniversalTime(),
            CloseAtUtc = DateTime.Parse(r.GetString(5)).ToUniversalTime(),
            Status = r.GetString(6),
            PostChannelId = r.IsDBNull(7) ? null : ulong.Parse(r.GetString(7)),
            ResultsMessageId = r.IsDBNull(8) ? null : ulong.Parse(r.GetString(8)),
            HotTakes = r.IsDBNull(9) ? null : r.GetString(9),
            InvitedCount = r.GetInt32(10),
            RespondedCount = r.GetInt32(11)
        };
    }

    public async Task<List<Survey>> GetAllSurveysAsync()
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, Description, CreatedByUserId, CreatedUtc, CloseAtUtc, Status, PostChannelId, ResultsMessageId, HotTakes, InvitedCount, RespondedCount
            FROM Surveys
            ORDER BY CreatedUtc DESC";

        var list = new List<Survey>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Survey
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Description = r.IsDBNull(2) ? null : r.GetString(2),
                CreatedByUserId = ulong.Parse(r.GetString(3)),
                CreatedUtc = DateTime.Parse(r.GetString(4)).ToUniversalTime(),
                CloseAtUtc = DateTime.Parse(r.GetString(5)).ToUniversalTime(),
                Status = r.GetString(6),
                PostChannelId = r.IsDBNull(7) ? null : ulong.Parse(r.GetString(7)),
                ResultsMessageId = r.IsDBNull(8) ? null : ulong.Parse(r.GetString(8)),
                HotTakes = r.IsDBNull(9) ? null : r.GetString(9),
                InvitedCount = r.GetInt32(10),
                RespondedCount = r.GetInt32(11)
            });
        }

        return list;
    }

    public async Task UpdateSurveyAsync(Survey survey)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            UPDATE Surveys
            SET Title = @t, Description = @d, Status = @s, PostChannelId = @pch, ResultsMessageId = @rmsg, HotTakes = @ht, InvitedCount = @inv, RespondedCount = @resp
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", survey.Id);
        cmd.Parameters.AddWithValue("@t", survey.Title);
        cmd.Parameters.AddWithValue("@d", (object?)survey.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", survey.Status);
        cmd.Parameters.AddWithValue("@pch", (object?)(survey.PostChannelId.HasValue ? survey.PostChannelId.Value.ToString() : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rmsg", (object?)(survey.ResultsMessageId.HasValue ? survey.ResultsMessageId.Value.ToString() : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ht", (object?)survey.HotTakes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inv", survey.InvitedCount);
        cmd.Parameters.AddWithValue("@resp", survey.RespondedCount);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SurveyQuestion> CreateQuestionAsync(SurveyQuestion question)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SurveyQuestions (Id, SurveyId, Order_Index, Text)
            VALUES (@id, @sid, @o, @t)";
        cmd.Parameters.AddWithValue("@id", question.Id);
        cmd.Parameters.AddWithValue("@sid", question.SurveyId);
        cmd.Parameters.AddWithValue("@o", question.Order);
        cmd.Parameters.AddWithValue("@t", question.Text);

        await cmd.ExecuteNonQueryAsync();
        return question;
    }

    public async Task<List<SurveyQuestion>> GetQuestionsBySurveyAsync(string surveyId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SurveyId, Order_Index, Text
            FROM SurveyQuestions
            WHERE SurveyId = @sid
            ORDER BY Order_Index";
        cmd.Parameters.AddWithValue("@sid", surveyId);

        var list = new List<SurveyQuestion>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SurveyQuestion
            {
                Id = r.GetString(0),
                SurveyId = r.GetString(1),
                Order = r.GetInt32(2),
                Text = r.GetString(3)
            });
        }

        return list;
    }

    public async Task<SurveyOption> CreateOptionAsync(SurveyOption option)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SurveyOptions (Id, QuestionId, Order_Index, Text, ResponseCount)
            VALUES (@id, @qid, @o, @t, @rc)";
        cmd.Parameters.AddWithValue("@id", option.Id);
        cmd.Parameters.AddWithValue("@qid", option.QuestionId);
        cmd.Parameters.AddWithValue("@o", option.Order);
        cmd.Parameters.AddWithValue("@t", option.Text);
        cmd.Parameters.AddWithValue("@rc", option.ResponseCount);

        await cmd.ExecuteNonQueryAsync();
        return option;
    }

    public async Task<List<SurveyOption>> GetOptionsByQuestionAsync(string questionId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, QuestionId, Order_Index, Text, ResponseCount
            FROM SurveyOptions
            WHERE QuestionId = @qid
            ORDER BY Order_Index";
        cmd.Parameters.AddWithValue("@qid", questionId);

        var list = new List<SurveyOption>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SurveyOption
            {
                Id = r.GetString(0),
                QuestionId = r.GetString(1),
                Order = r.GetInt32(2),
                Text = r.GetString(3),
                ResponseCount = r.GetInt32(4)
            });
        }

        return list;
    }

    public async Task<SurveyResponse> CreateResponseAsync(SurveyResponse response)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO SurveyResponses (Id, SurveyId, UserId, QuestionId, SelectedOptionId, SubmittedUtc)
            VALUES (@id, @sid, @uid, @qid, @oid, @subm)";
        cmd.Parameters.AddWithValue("@id", response.Id);
        cmd.Parameters.AddWithValue("@sid", response.SurveyId);
        cmd.Parameters.AddWithValue("@uid", response.UserId.ToString());
        cmd.Parameters.AddWithValue("@qid", response.QuestionId);
        cmd.Parameters.AddWithValue("@oid", response.SelectedOptionId);
        cmd.Parameters.AddWithValue("@subm", response.SubmittedUtc.ToString("O"));

        await cmd.ExecuteNonQueryAsync();

        // Update option response count
        await using var updateCmd = con.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE SurveyOptions
            SET ResponseCount = (
                SELECT COUNT(*)
                FROM SurveyResponses
                WHERE SelectedOptionId = @oid
            )
            WHERE Id = @oid";
        updateCmd.Parameters.AddWithValue("@oid", response.SelectedOptionId);
        await updateCmd.ExecuteNonQueryAsync();

        return response;
    }

    public async Task<SurveyResponse?> GetResponseAsync(string surveyId, ulong userId, string questionId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SurveyId, UserId, QuestionId, SelectedOptionId, SubmittedUtc
            FROM SurveyResponses
            WHERE SurveyId = @sid AND UserId = @uid AND QuestionId = @qid";
        cmd.Parameters.AddWithValue("@sid", surveyId);
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        cmd.Parameters.AddWithValue("@qid", questionId);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new SurveyResponse
        {
            Id = r.GetString(0),
            SurveyId = r.GetString(1),
            UserId = ulong.Parse(r.GetString(2)),
            QuestionId = r.GetString(3),
            SelectedOptionId = r.GetString(4),
            SubmittedUtc = DateTime.Parse(r.GetString(5)).ToUniversalTime()
        };
    }

    public async Task<List<SurveyResponse>> GetResponsesBySurveyAsync(string surveyId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SurveyId, UserId, QuestionId, SelectedOptionId, SubmittedUtc
            FROM SurveyResponses
            WHERE SurveyId = @sid";
        cmd.Parameters.AddWithValue("@sid", surveyId);

        var list = new List<SurveyResponse>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SurveyResponse
            {
                Id = r.GetString(0),
                SurveyId = r.GetString(1),
                UserId = ulong.Parse(r.GetString(2)),
                QuestionId = r.GetString(3),
                SelectedOptionId = r.GetString(4),
                SubmittedUtc = DateTime.Parse(r.GetString(5)).ToUniversalTime()
            });
        }

        return list;
    }

    public async Task<SurveyFeedback?> CreateFeedbackAsync(SurveyFeedback feedback)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO SurveyFeedback (Id, SurveyId, UserId, FeedbackText, SubmittedUtc)
            VALUES (@id, @sid, @uid, @txt, @subm)";
        cmd.Parameters.AddWithValue("@id", feedback.Id);
        cmd.Parameters.AddWithValue("@sid", feedback.SurveyId);
        cmd.Parameters.AddWithValue("@uid", feedback.UserId.ToString());
        cmd.Parameters.AddWithValue("@txt", feedback.FeedbackText);
        cmd.Parameters.AddWithValue("@subm", feedback.SubmittedUtc.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
        return feedback;
    }

    public async Task<List<SurveyFeedback>> GetFeedbackBySurveyAsync(string surveyId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SurveyId, UserId, FeedbackText, SubmittedUtc
            FROM SurveyFeedback
            WHERE SurveyId = @sid";
        cmd.Parameters.AddWithValue("@sid", surveyId);

        var list = new List<SurveyFeedback>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new SurveyFeedback
            {
                Id = r.GetString(0),
                SurveyId = r.GetString(1),
                UserId = ulong.Parse(r.GetString(2)),
                FeedbackText = r.GetString(3),
                SubmittedUtc = DateTime.Parse(r.GetString(4)).ToUniversalTime()
            });
        }

        return list;
    }

    public async Task<List<ulong>> GetSurveyRespondersAsync(string surveyId)
    {
        await using var con = Open();
        await con.OpenAsync();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT UserId
            FROM SurveyResponses
            WHERE SurveyId = @sid";
        cmd.Parameters.AddWithValue("@sid", surveyId);

        var list = new List<ulong>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(ulong.Parse(r.GetString(0)));
        }

        return list;
    }}