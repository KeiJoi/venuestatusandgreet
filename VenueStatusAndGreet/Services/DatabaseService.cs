using System.Globalization;
using Dalamud.Plugin.Services;
using Microsoft.Data.Sqlite;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Services;

public sealed class DatabaseService : IDisposable
{
    private readonly object syncRoot = new();
    private readonly string dbPath;
    private readonly IPluginLog log;

    private long? currentNightId;
    private long? currentSessionId;
    private DateOnly currentNightDate;

    public DatabaseService(string dbPath, IPluginLog log)
    {
        this.dbPath = dbPath;
        this.log = log;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.dbPath) ?? ".");

        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var transaction = connection.BeginTransaction();

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS venue_nights (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    night_date_local TEXT NOT NULL UNIQUE,
                    venue_name TEXT NOT NULL,
                    venue_address TEXT NOT NULL,
                    is_open INTEGER NOT NULL DEFAULT 0,
                    opened_at_utc TEXT NULL,
                    closed_at_utc TEXT NULL,
                    created_at_utc TEXT NOT NULL
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS visitor_night_stats (
                    night_id INTEGER NOT NULL,
                    character_name TEXT NOT NULL,
                    home_world TEXT NOT NULL,
                    visits INTEGER NOT NULL DEFAULT 0,
                    total_seconds INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc TEXT NULL,
                    last_seen_utc TEXT NULL,
                    last_visit_start_utc TEXT NULL,
                    greeted INTEGER NOT NULL DEFAULT 0,
                    currently_present INTEGER NOT NULL DEFAULT 0,
                    last_object_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (night_id, character_name, home_world),
                    FOREIGN KEY (night_id) REFERENCES venue_nights(id) ON DELETE CASCADE
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS guest_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    night_id INTEGER NOT NULL,
                    sample_time_utc TEXT NOT NULL,
                    guest_count INTEGER NOT NULL,
                    FOREIGN KEY (night_id) REFERENCES venue_nights(id) ON DELETE CASCADE
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS venue_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    night_id INTEGER NOT NULL,
                    venue_name TEXT NOT NULL,
                    venue_address TEXT NOT NULL,
                    opened_at_utc TEXT NOT NULL,
                    closed_at_utc TEXT NULL,
                    FOREIGN KEY (night_id) REFERENCES venue_nights(id) ON DELETE CASCADE
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS session_visitors (
                    session_id INTEGER NOT NULL,
                    character_name TEXT NOT NULL,
                    home_world TEXT NOT NULL,
                    visits INTEGER NOT NULL DEFAULT 0,
                    total_seconds INTEGER NOT NULL DEFAULT 0,
                    first_seen_utc TEXT NULL,
                    last_seen_utc TEXT NULL,
                    last_visit_start_utc TEXT NULL,
                    greeted INTEGER NOT NULL DEFAULT 0,
                    currently_present INTEGER NOT NULL DEFAULT 0,
                    last_object_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (session_id, character_name, home_world),
                    FOREIGN KEY (session_id) REFERENCES venue_sessions(id) ON DELETE CASCADE
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS session_guest_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id INTEGER NOT NULL,
                    sample_time_utc TEXT NOT NULL,
                    guest_count INTEGER NOT NULL,
                    FOREIGN KEY (session_id) REFERENCES venue_sessions(id) ON DELETE CASCADE
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS greet_presets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    line1 TEXT NOT NULL DEFAULT '',
                    line2 TEXT NOT NULL DEFAULT '',
                    line3 TEXT NOT NULL DEFAULT ''
                );");

            this.ExecuteNonQuery(connection, transaction, @"
                CREATE TABLE IF NOT EXISTS hotbar_slots (
                    slot INTEGER PRIMARY KEY,
                    preset_id INTEGER NULL,
                    FOREIGN KEY (preset_id) REFERENCES greet_presets(id) ON DELETE SET NULL
                );");

            transaction.Commit();
        }
    }

    public void EnsureHotbarSlots()
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            for (var slot = 1; slot <= 5; slot++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR IGNORE INTO hotbar_slots (slot, preset_id)
                    VALUES (@slot, NULL);";
                command.Parameters.AddWithValue("@slot", slot);
                command.ExecuteNonQuery();
            }
        }
    }

    public void SetVenueInfo(string venueName, string venueAddress, DateTime nowUtc)
    {
        lock (this.syncRoot)
        {
            _ = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
        }
    }

    public void SetVenueOpen(bool isOpen, string venueName, string venueAddress, DateTime nowUtc)
    {
        lock (this.syncRoot)
        {
            var nightId = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE venue_nights
                SET is_open = @is_open,
                    opened_at_utc = CASE WHEN @is_open = 1 AND opened_at_utc IS NULL THEN @opened_now ELSE opened_at_utc END,
                    closed_at_utc = CASE WHEN @is_open = 0 THEN @closed_now ELSE NULL END
                WHERE id = @night_id;";
            command.Parameters.AddWithValue("@is_open", isOpen ? 1 : 0);
            command.Parameters.AddWithValue("@opened_now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@closed_now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@night_id", nightId);
            _ = command.ExecuteNonQuery();

            if (isOpen)
            {
                using var createSession = connection.CreateCommand();
                createSession.CommandText = @"
                    INSERT INTO venue_sessions (night_id, venue_name, venue_address, opened_at_utc)
                    VALUES (@night_id, @name, @address, @opened_at);";
                createSession.Parameters.AddWithValue("@night_id", nightId);
                createSession.Parameters.AddWithValue("@name", venueName.Trim());
                createSession.Parameters.AddWithValue("@address", venueAddress.Trim());
                createSession.Parameters.AddWithValue("@opened_at", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                _ = createSession.ExecuteNonQuery();
                using var lastId = connection.CreateCommand();
                lastId.CommandText = "SELECT last_insert_rowid();";
                this.currentSessionId = Convert.ToInt64(lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            else
            {
                this.CloseAllPresentInternal(connection, nightId, nowUtc);
                if (this.currentSessionId is long sessionId)
                {
                    this.CloseAllPresentInSessionInternal(connection, sessionId, nowUtc);
                    using var closeSession = connection.CreateCommand();
                    closeSession.CommandText = @"
                        UPDATE venue_sessions
                        SET closed_at_utc = @closed
                        WHERE id = @session_id;";
                    closeSession.Parameters.AddWithValue("@closed", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                    closeSession.Parameters.AddWithValue("@session_id", sessionId);
                    _ = closeSession.ExecuteNonQuery();
                }

                this.currentSessionId = null;
            }
        }
    }

    public VisitorPresenceChange MarkVisitorPresent(GuestIdentity guest, DateTime nowUtc, ulong objectId, string venueName, string venueAddress)
    {
        lock (this.syncRoot)
        {
            var nightId = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
            using var connection = this.OpenConnection();
            VisitorPresenceChange change;

            using var select = connection.CreateCommand();
            select.CommandText = @"
                SELECT visits, currently_present
                FROM visitor_night_stats
                WHERE night_id = @night_id
                  AND character_name = @name
                  AND home_world = @world;";
            select.Parameters.AddWithValue("@night_id", nightId);
            select.Parameters.AddWithValue("@name", guest.Name);
            select.Parameters.AddWithValue("@world", guest.HomeWorld);
            using var reader = select.ExecuteReader();

            if (!reader.Read())
            {
                reader.Close();
                using var insert = connection.CreateCommand();
                insert.CommandText = @"
                    INSERT INTO visitor_night_stats (
                        night_id, character_name, home_world, visits, total_seconds,
                        first_seen_utc, last_seen_utc, last_visit_start_utc,
                        greeted, currently_present, last_object_id)
                    VALUES (
                        @night_id, @name, @world, 1, 0,
                        @now, @now, @now, 0, 1, @object_id);";
                insert.Parameters.AddWithValue("@night_id", nightId);
                insert.Parameters.AddWithValue("@name", guest.Name);
                insert.Parameters.AddWithValue("@world", guest.HomeWorld);
                insert.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
                _ = insert.ExecuteNonQuery();
                change = new VisitorPresenceChange(BecamePresent: true, IsFirstVisitTonight: true);
            }
            else
            {
                var visits = reader.GetInt32(0);
                var currentlyPresent = reader.GetInt32(1) == 1;
                reader.Close();

                if (currentlyPresent)
                {
                    using var keepAlive = connection.CreateCommand();
                    keepAlive.CommandText = @"
                        UPDATE visitor_night_stats
                        SET last_seen_utc = @now,
                            last_object_id = @object_id
                        WHERE night_id = @night_id
                          AND character_name = @name
                          AND home_world = @world;";
                    keepAlive.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                    keepAlive.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
                    keepAlive.Parameters.AddWithValue("@night_id", nightId);
                    keepAlive.Parameters.AddWithValue("@name", guest.Name);
                    keepAlive.Parameters.AddWithValue("@world", guest.HomeWorld);
                    _ = keepAlive.ExecuteNonQuery();
                    change = new VisitorPresenceChange(BecamePresent: false, IsFirstVisitTonight: false);
                }
                else
                {
                    var nextVisits = visits + 1;
                    using var update = connection.CreateCommand();
                    update.CommandText = @"
                        UPDATE visitor_night_stats
                        SET visits = @visits,
                            currently_present = 1,
                            last_seen_utc = @now,
                            last_visit_start_utc = @now,
                            last_object_id = @object_id
                        WHERE night_id = @night_id
                          AND character_name = @name
                          AND home_world = @world;";
                    update.Parameters.AddWithValue("@visits", nextVisits);
                    update.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                    update.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
                    update.Parameters.AddWithValue("@night_id", nightId);
                    update.Parameters.AddWithValue("@name", guest.Name);
                    update.Parameters.AddWithValue("@world", guest.HomeWorld);
                    _ = update.ExecuteNonQuery();
                    change = new VisitorPresenceChange(BecamePresent: true, IsFirstVisitTonight: nextVisits == 1);
                }
            }

            if (this.currentSessionId is long sessionId)
            {
                this.UpsertSessionVisitorPresence(connection, sessionId, guest, nowUtc, objectId);
            }

            return change;
        }
    }

    public void MarkVisitorAbsent(GuestIdentity guest, DateTime nowUtc, string venueName, string venueAddress)
    {
        lock (this.syncRoot)
        {
            var nightId = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
            using var connection = this.OpenConnection();
            this.MarkVisitorAbsentInternal(connection, nightId, guest, nowUtc);
            if (this.currentSessionId is long sessionId)
            {
                this.MarkSessionVisitorAbsentInternal(connection, sessionId, guest, nowUtc);
            }
        }
    }

    public void MarkVisitorGreeted(GuestIdentity guest, bool greeted, DateTime nowUtc, string venueName, string venueAddress)
    {
        lock (this.syncRoot)
        {
            var nightId = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE visitor_night_stats
                SET greeted = @greeted
                WHERE night_id = @night_id
                  AND character_name = @name
                  AND home_world = @world;";
            command.Parameters.AddWithValue("@greeted", greeted ? 1 : 0);
            command.Parameters.AddWithValue("@night_id", nightId);
            command.Parameters.AddWithValue("@name", guest.Name);
            command.Parameters.AddWithValue("@world", guest.HomeWorld);
            _ = command.ExecuteNonQuery();

            if (this.currentSessionId is long sessionId)
            {
                using var sessionCommand = connection.CreateCommand();
                sessionCommand.CommandText = @"
                    UPDATE session_visitors
                    SET greeted = @greeted
                    WHERE session_id = @session_id
                      AND character_name = @name
                      AND home_world = @world;";
                sessionCommand.Parameters.AddWithValue("@greeted", greeted ? 1 : 0);
                sessionCommand.Parameters.AddWithValue("@session_id", sessionId);
                sessionCommand.Parameters.AddWithValue("@name", guest.Name);
                sessionCommand.Parameters.AddWithValue("@world", guest.HomeWorld);
                _ = sessionCommand.ExecuteNonQuery();
            }
        }
    }

    public void RecordGuestSample(int guestCount, DateTime nowUtc, string venueName, string venueAddress)
    {
        lock (this.syncRoot)
        {
            var nightId = this.EnsureNightRowInternal(venueName, venueAddress, nowUtc);
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO guest_samples (night_id, sample_time_utc, guest_count)
                VALUES (@night_id, @sample_time, @guest_count);";
            command.Parameters.AddWithValue("@night_id", nightId);
            command.Parameters.AddWithValue("@sample_time", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@guest_count", guestCount);
            _ = command.ExecuteNonQuery();

            if (this.currentSessionId is long sessionId)
            {
                using var sessionSample = connection.CreateCommand();
                sessionSample.CommandText = @"
                    INSERT INTO session_guest_samples (session_id, sample_time_utc, guest_count)
                    VALUES (@session_id, @sample_time, @guest_count);";
                sessionSample.Parameters.AddWithValue("@session_id", sessionId);
                sessionSample.Parameters.AddWithValue("@sample_time", nowUtc.ToString("O", CultureInfo.InvariantCulture));
                sessionSample.Parameters.AddWithValue("@guest_count", guestCount);
                _ = sessionSample.ExecuteNonQuery();
            }
        }
    }

    public List<VisitorNightSummary> GetTonightVisitors()
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            var sessionId = this.GetActiveOrLatestSessionIdInternal(connection);
            if (sessionId is null)
            {
                return [];
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT character_name, home_world, visits, total_seconds, currently_present, greeted, last_object_id
                FROM session_visitors
                WHERE session_id = @session_id;";
            command.Parameters.AddWithValue("@session_id", sessionId.Value);
            using var reader = command.ExecuteReader();
            var list = new List<VisitorNightSummary>();
            while (reader.Read())
            {
                list.Add(new VisitorNightSummary
                {
                    NightDate = this.currentNightDate == default ? DateOnly.FromDateTime(DateTime.Now) : this.currentNightDate,
                    CharacterName = reader.GetString(0),
                    HomeWorld = reader.GetString(1),
                    Visits = reader.GetInt32(2),
                    TotalTime = TimeSpan.FromSeconds(reader.GetInt64(3)),
                    IsPresent = reader.GetInt32(4) == 1,
                    Greeted = reader.GetInt32(5) == 1,
                    LastObjectId = Convert.ToUInt64(reader.GetInt64(6)),
                });
            }

            return list
                .OrderByDescending(static x => x.IsPresent)
                .ThenBy(static x => x.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public NightSummary GetTonightSummary()
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            var sessionId = this.GetActiveOrLatestSessionIdInternal(connection);
            if (sessionId is null)
            {
                return new NightSummary { NightDate = DateOnly.FromDateTime(DateTime.Now) };
            }

            var summary = new NightSummary { NightDate = this.currentNightDate };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT
                        SUM(CASE WHEN currently_present = 1 THEN 1 ELSE 0 END),
                        COUNT(*),
                        COALESCE(SUM(visits), 0),
                        COALESCE(SUM(total_seconds), 0)
                    FROM session_visitors
                    WHERE session_id = @session_id;";
                command.Parameters.AddWithValue("@session_id", sessionId.Value);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    summary.CurrentGuests = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    summary.UniqueGuests = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    summary.TotalVisits = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    summary.TotalGuestTime = TimeSpan.FromSeconds(reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT MAX(guest_count), MIN(guest_count)
                    FROM session_guest_samples
                    WHERE session_id = @session_id;";
                command.Parameters.AddWithValue("@session_id", sessionId.Value);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var hasSamples = !reader.IsDBNull(0) && !reader.IsDBNull(1);
                    summary.MaxGuests = hasSamples ? reader.GetInt32(0) : summary.CurrentGuests;
                    summary.MinGuests = hasSamples ? reader.GetInt32(1) : summary.CurrentGuests;
                }
            }

            return summary;
        }
    }

    public List<GuestSampleRow> GetTonightSamples(int maxRows = 288)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            var sessionId = this.GetActiveOrLatestSessionIdInternal(connection);
            if (sessionId is null)
            {
                return [];
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT sample_time_utc, guest_count
                FROM session_guest_samples
                WHERE session_id = @session_id
                ORDER BY sample_time_utc DESC
                LIMIT @max_rows;";
            command.Parameters.AddWithValue("@session_id", sessionId.Value);
            command.Parameters.AddWithValue("@max_rows", maxRows);
            using var reader = command.ExecuteReader();
            var rows = new List<GuestSampleRow>();
            while (reader.Read())
            {
                if (!DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc))
                {
                    continue;
                }

                rows.Add(new GuestSampleRow
                {
                    NightDate = this.currentNightDate,
                    SampleTimeLocal = utc.ToLocalTime(),
                    GuestCount = reader.GetInt32(1),
                });
            }

            rows.Reverse();
            return rows;
        }
    }

    public long? GetCurrentSessionId()
    {
        lock (this.syncRoot)
        {
            return this.currentSessionId;
        }
    }

    public List<VenueSessionEntry> GetRecentSessions(int maxRows = 100)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT s.id, s.opened_at_utc, s.closed_at_utc, n.night_date_local, s.venue_name
                FROM venue_sessions s
                INNER JOIN venue_nights n ON n.id = s.night_id
                ORDER BY s.opened_at_utc DESC
                LIMIT @max_rows;";
            command.Parameters.AddWithValue("@max_rows", maxRows);
            using var reader = command.ExecuteReader();
            var rows = new List<VenueSessionEntry>();
            while (reader.Read())
            {
                if (!DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var openedUtc))
                {
                    continue;
                }

                DateTime? closedLocal = null;
                if (!reader.IsDBNull(2) &&
                    DateTime.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var closedUtc))
                {
                    closedLocal = closedUtc.ToLocalTime();
                }

                if (!DateOnly.TryParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nightDate))
                {
                    nightDate = DateOnly.FromDateTime(openedUtc.ToLocalTime());
                }

                rows.Add(new VenueSessionEntry
                {
                    SessionId = reader.GetInt64(0),
                    OpenedAtLocal = openedUtc.ToLocalTime(),
                    ClosedAtLocal = closedLocal,
                    NightDate = nightDate,
                    VenueName = reader.GetString(4),
                });
            }

            return rows;
        }
    }

    public bool DeleteSession(long sessionId)
    {
        lock (this.syncRoot)
        {
            if (this.currentSessionId == sessionId)
            {
                this.log.Warning($"Refusing to delete currently active session id={sessionId}.");
                return false;
            }

            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM venue_sessions
                WHERE id = @session_id;";
            command.Parameters.AddWithValue("@session_id", sessionId);
            var affected = command.ExecuteNonQuery();
            var deleted = affected > 0;
            if (deleted)
            {
                this.log.Information($"Deleted venue session id={sessionId}.");
            }
            else
            {
                this.log.Warning($"No venue session row deleted for id={sessionId}.");
            }

            return deleted;
        }
    }

    public List<VisitorNightSummary> GetVisitorsForSession(long sessionId)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT character_name, home_world, visits, total_seconds, currently_present, greeted, last_object_id
                FROM session_visitors
                WHERE session_id = @session_id;";
            command.Parameters.AddWithValue("@session_id", sessionId);
            using var reader = command.ExecuteReader();
            var list = new List<VisitorNightSummary>();
            while (reader.Read())
            {
                list.Add(new VisitorNightSummary
                {
                    NightDate = this.currentNightDate == default ? DateOnly.FromDateTime(DateTime.Now) : this.currentNightDate,
                    CharacterName = reader.GetString(0),
                    HomeWorld = reader.GetString(1),
                    Visits = reader.GetInt32(2),
                    TotalTime = TimeSpan.FromSeconds(reader.GetInt64(3)),
                    IsPresent = reader.GetInt32(4) == 1,
                    Greeted = reader.GetInt32(5) == 1,
                    LastObjectId = Convert.ToUInt64(reader.GetInt64(6)),
                });
            }

            return list
                .OrderByDescending(static x => x.IsPresent)
                .ThenBy(static x => x.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public NightSummary GetSummaryForSession(long sessionId)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            var summary = new NightSummary { NightDate = this.currentNightDate == default ? DateOnly.FromDateTime(DateTime.Now) : this.currentNightDate };

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT
                        SUM(CASE WHEN currently_present = 1 THEN 1 ELSE 0 END),
                        COUNT(*),
                        COALESCE(SUM(visits), 0),
                        COALESCE(SUM(total_seconds), 0)
                    FROM session_visitors
                    WHERE session_id = @session_id;";
                command.Parameters.AddWithValue("@session_id", sessionId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    summary.CurrentGuests = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    summary.UniqueGuests = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    summary.TotalVisits = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    summary.TotalGuestTime = TimeSpan.FromSeconds(reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT MAX(guest_count), MIN(guest_count)
                    FROM session_guest_samples
                    WHERE session_id = @session_id;";
                command.Parameters.AddWithValue("@session_id", sessionId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var hasSamples = !reader.IsDBNull(0) && !reader.IsDBNull(1);
                    summary.MaxGuests = hasSamples ? reader.GetInt32(0) : summary.CurrentGuests;
                    summary.MinGuests = hasSamples ? reader.GetInt32(1) : summary.CurrentGuests;
                }
            }

            return summary;
        }
    }

    public List<GuestSampleRow> GetSamplesForSession(long sessionId, int maxRows = 288)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT sample_time_utc, guest_count
                FROM session_guest_samples
                WHERE session_id = @session_id
                ORDER BY sample_time_utc DESC
                LIMIT @max_rows;";
            command.Parameters.AddWithValue("@session_id", sessionId);
            command.Parameters.AddWithValue("@max_rows", maxRows);
            using var reader = command.ExecuteReader();
            var rows = new List<GuestSampleRow>();
            while (reader.Read())
            {
                if (!DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc))
                {
                    continue;
                }

                rows.Add(new GuestSampleRow
                {
                    NightDate = this.currentNightDate == default ? DateOnly.FromDateTime(DateTime.Now) : this.currentNightDate,
                    SampleTimeLocal = utc.ToLocalTime(),
                    GuestCount = reader.GetInt32(1),
                });
            }

            rows.Reverse();
            return rows;
        }
    }

    public List<DailyStatRow> GetDailyStats(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    n.night_date_local,
                    COALESCE(MAX(s.guest_count), 0),
                    COALESCE(MIN(s.guest_count), 0),
                    COUNT(v.character_name),
                    COALESCE(SUM(v.visits), 0),
                    COALESCE(SUM(v.total_seconds), 0)
                FROM venue_nights n
                LEFT JOIN guest_samples s ON s.night_id = n.id
                LEFT JOIN visitor_night_stats v ON v.night_id = n.id
                WHERE n.night_date_local BETWEEN @from_date AND @to_date
                GROUP BY n.id, n.night_date_local
                ORDER BY n.night_date_local;";
            command.Parameters.AddWithValue("@from_date", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@to_date", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            var rows = new List<DailyStatRow>();
            while (reader.Read())
            {
                if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                rows.Add(new DailyStatRow
                {
                    NightDate = date,
                    MaxGuests = reader.GetInt32(1),
                    MinGuests = reader.GetInt32(2),
                    UniqueGuests = reader.GetInt32(3),
                    TotalVisits = reader.GetInt32(4),
                    TotalGuestTime = TimeSpan.FromSeconds(reader.GetInt64(5)),
                });
            }

            return rows;
        }
    }

    public List<VisitorRangeRow> GetVisitorsForRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    n.night_date_local,
                    v.character_name,
                    v.home_world,
                    v.visits,
                    v.total_seconds,
                    v.greeted
                FROM visitor_night_stats v
                INNER JOIN venue_nights n ON n.id = v.night_id
                WHERE n.night_date_local BETWEEN @from_date AND @to_date
                ORDER BY n.night_date_local, v.character_name;";
            command.Parameters.AddWithValue("@from_date", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@to_date", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            var rows = new List<VisitorRangeRow>();
            while (reader.Read())
            {
                if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                rows.Add(new VisitorRangeRow
                {
                    NightDate = date,
                    CharacterName = reader.GetString(1),
                    HomeWorld = reader.GetString(2),
                    Visits = reader.GetInt32(3),
                    TotalTime = TimeSpan.FromSeconds(reader.GetInt64(4)),
                    Greeted = reader.GetInt32(5) == 1,
                });
            }

            return rows;
        }
    }

    public List<GuestSampleRow> GetSamplesForRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    n.night_date_local,
                    s.sample_time_utc,
                    s.guest_count
                FROM guest_samples s
                INNER JOIN venue_nights n ON n.id = s.night_id
                WHERE n.night_date_local BETWEEN @from_date AND @to_date
                ORDER BY s.sample_time_utc;";
            command.Parameters.AddWithValue("@from_date", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@to_date", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            var rows = new List<GuestSampleRow>();
            while (reader.Read())
            {
                if (!DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                if (!DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sampleUtc))
                {
                    continue;
                }

                rows.Add(new GuestSampleRow
                {
                    NightDate = date,
                    SampleTimeLocal = sampleUtc.ToLocalTime(),
                    GuestCount = reader.GetInt32(2),
                });
            }

            return rows;
        }
    }

    public List<GreetPreset> GetGreetPresets()
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, name, line1, line2, line3
                FROM greet_presets
                ORDER BY name;";
            using var reader = command.ExecuteReader();
            var rows = new List<GreetPreset>();
            while (reader.Read())
            {
                rows.Add(new GreetPreset
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Line1 = reader.GetString(2),
                    Line2 = reader.GetString(3),
                    Line3 = reader.GetString(4),
                });
            }

            return rows;
        }
    }

    public GreetPreset? GetPresetById(int presetId)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, name, line1, line2, line3
                FROM greet_presets
                WHERE id = @id;";
            command.Parameters.AddWithValue("@id", presetId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new GreetPreset
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Line1 = reader.GetString(2),
                Line2 = reader.GetString(3),
                Line3 = reader.GetString(4),
            };
        }
    }

    public int SavePreset(string name, string line1, string line2, string line3)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO greet_presets (name, line1, line2, line3)
                VALUES (@name, @line1, @line2, @line3)
                ON CONFLICT(name) DO UPDATE SET
                    line1 = excluded.line1,
                    line2 = excluded.line2,
                    line3 = excluded.line3;";
            command.Parameters.AddWithValue("@name", name.Trim());
            command.Parameters.AddWithValue("@line1", line1.Trim());
            command.Parameters.AddWithValue("@line2", line2.Trim());
            command.Parameters.AddWithValue("@line3", line3.Trim());
            _ = command.ExecuteNonQuery();

            using var select = connection.CreateCommand();
            select.CommandText = "SELECT id FROM greet_presets WHERE name = @name;";
            select.Parameters.AddWithValue("@name", name.Trim());
            var result = select.ExecuteScalar();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    public void DeletePreset(int presetId)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var clearSlots = connection.CreateCommand())
            {
                clearSlots.Transaction = transaction;
                clearSlots.CommandText = @"
                    UPDATE hotbar_slots
                    SET preset_id = NULL
                    WHERE preset_id = @preset_id;";
                clearSlots.Parameters.AddWithValue("@preset_id", presetId);
                _ = clearSlots.ExecuteNonQuery();
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM greet_presets WHERE id = @preset_id;";
                delete.Parameters.AddWithValue("@preset_id", presetId);
                _ = delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public Dictionary<int, int?> GetHotbarAssignments()
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT slot, preset_id
                FROM hotbar_slots
                ORDER BY slot;";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<int, int?>();
            while (reader.Read())
            {
                result[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            }

            for (var slot = 1; slot <= 5; slot++)
            {
                if (!result.ContainsKey(slot))
                {
                    result[slot] = null;
                }
            }

            return result;
        }
    }

    public void SetHotbarAssignment(int slot, int? presetId)
    {
        lock (this.syncRoot)
        {
            using var connection = this.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO hotbar_slots (slot, preset_id)
                VALUES (@slot, @preset_id)
                ON CONFLICT(slot) DO UPDATE SET
                    preset_id = excluded.preset_id;";
            command.Parameters.AddWithValue("@slot", slot);
            if (presetId is int id)
            {
                command.Parameters.AddWithValue("@preset_id", id);
            }
            else
            {
                _ = command.Parameters.AddWithValue("@preset_id", DBNull.Value);
            }

            _ = command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private long EnsureNightRowInternal(string venueName, string venueAddress, DateTime nowUtc)
    {
        var localDate = DateOnly.FromDateTime(nowUtc.ToLocalTime());
        if (this.currentNightId is long cachedId && this.currentNightDate == localDate)
        {
            using var connection = this.OpenConnection();
            using var update = connection.CreateCommand();
            update.CommandText = @"
                UPDATE venue_nights
                SET venue_name = @name, venue_address = @address
                WHERE id = @id;";
            update.Parameters.AddWithValue("@name", venueName.Trim());
            update.Parameters.AddWithValue("@address", venueAddress.Trim());
            update.Parameters.AddWithValue("@id", cachedId);
            _ = update.ExecuteNonQuery();
            return cachedId;
        }

        using var insertConnection = this.OpenConnection();
        using (var insertOrIgnore = insertConnection.CreateCommand())
        {
            insertOrIgnore.CommandText = @"
                INSERT OR IGNORE INTO venue_nights
                (night_date_local, venue_name, venue_address, created_at_utc)
                VALUES (@date, @name, @address, @created);";
            insertOrIgnore.Parameters.AddWithValue("@date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertOrIgnore.Parameters.AddWithValue("@name", venueName.Trim());
            insertOrIgnore.Parameters.AddWithValue("@address", venueAddress.Trim());
            insertOrIgnore.Parameters.AddWithValue("@created", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            _ = insertOrIgnore.ExecuteNonQuery();
        }

        using var select = insertConnection.CreateCommand();
        select.CommandText = @"
            SELECT id
            FROM venue_nights
            WHERE night_date_local = @date;";
        select.Parameters.AddWithValue("@date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var result = select.ExecuteScalar();
        if (result is null)
        {
            throw new InvalidOperationException("Failed to resolve nightly venue row.");
        }

        this.currentNightId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        this.currentNightDate = localDate;
        return this.currentNightId.Value;
    }

    private long? GetCurrentNightIdInternal()
    {
        var localDate = DateOnly.FromDateTime(DateTime.Now);
        if (this.currentNightId is long id && this.currentNightDate == localDate)
        {
            return id;
        }

        using var connection = this.OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT id
            FROM venue_nights
            WHERE night_date_local = @date;";
        select.Parameters.AddWithValue("@date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var result = select.ExecuteScalar();
        if (result is null)
        {
            this.currentNightId = null;
            this.currentNightDate = localDate;
            return null;
        }

        this.currentNightId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        this.currentNightDate = localDate;
        return this.currentNightId;
    }

    private void CloseAllPresentInternal(SqliteConnection connection, long nightId, DateTime nowUtc)
    {
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT character_name, home_world
            FROM visitor_night_stats
            WHERE night_id = @night_id
              AND currently_present = 1;";
        select.Parameters.AddWithValue("@night_id", nightId);
        using var reader = select.ExecuteReader();
        var guests = new List<GuestIdentity>();
        while (reader.Read())
        {
            guests.Add(new GuestIdentity(reader.GetString(0), reader.GetString(1)));
        }

        foreach (var guest in guests)
        {
            this.MarkVisitorAbsentInternal(connection, nightId, guest, nowUtc);
        }
    }

    private long? GetActiveOrLatestSessionIdInternal(SqliteConnection connection)
    {
        if (this.currentSessionId is long current)
        {
            return current;
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id
            FROM venue_sessions
            ORDER BY opened_at_utc DESC
            LIMIT 1;";
        var result = command.ExecuteScalar();
        if (result is null)
        {
            return null;
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private void UpsertSessionVisitorPresence(SqliteConnection connection, long sessionId, GuestIdentity guest, DateTime nowUtc, ulong objectId)
    {
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT visits, currently_present
            FROM session_visitors
            WHERE session_id = @session_id
              AND character_name = @name
              AND home_world = @world;";
        select.Parameters.AddWithValue("@session_id", sessionId);
        select.Parameters.AddWithValue("@name", guest.Name);
        select.Parameters.AddWithValue("@world", guest.HomeWorld);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            reader.Close();
            using var insert = connection.CreateCommand();
            insert.CommandText = @"
                INSERT INTO session_visitors (
                    session_id, character_name, home_world, visits, total_seconds,
                    first_seen_utc, last_seen_utc, last_visit_start_utc,
                    greeted, currently_present, last_object_id)
                VALUES (
                    @session_id, @name, @world, 1, 0,
                    @now, @now, @now, 0, 1, @object_id);";
            insert.Parameters.AddWithValue("@session_id", sessionId);
            insert.Parameters.AddWithValue("@name", guest.Name);
            insert.Parameters.AddWithValue("@world", guest.HomeWorld);
            insert.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
            _ = insert.ExecuteNonQuery();
            return;
        }

        var visits = reader.GetInt32(0);
        var currentlyPresent = reader.GetInt32(1) == 1;
        reader.Close();

        if (currentlyPresent)
        {
            using var keepAlive = connection.CreateCommand();
            keepAlive.CommandText = @"
                UPDATE session_visitors
                SET last_seen_utc = @now,
                    last_object_id = @object_id
                WHERE session_id = @session_id
                  AND character_name = @name
                  AND home_world = @world;";
            keepAlive.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            keepAlive.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
            keepAlive.Parameters.AddWithValue("@session_id", sessionId);
            keepAlive.Parameters.AddWithValue("@name", guest.Name);
            keepAlive.Parameters.AddWithValue("@world", guest.HomeWorld);
            _ = keepAlive.ExecuteNonQuery();
            return;
        }

        using var update = connection.CreateCommand();
        update.CommandText = @"
            UPDATE session_visitors
            SET visits = @visits,
                currently_present = 1,
                last_seen_utc = @now,
                last_visit_start_utc = @now,
                last_object_id = @object_id
            WHERE session_id = @session_id
              AND character_name = @name
              AND home_world = @world;";
        update.Parameters.AddWithValue("@visits", visits + 1);
        update.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("@object_id", Convert.ToInt64(objectId));
        update.Parameters.AddWithValue("@session_id", sessionId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        _ = update.ExecuteNonQuery();
    }

    private void CloseAllPresentInSessionInternal(SqliteConnection connection, long sessionId, DateTime nowUtc)
    {
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT character_name, home_world
            FROM session_visitors
            WHERE session_id = @session_id
              AND currently_present = 1;";
        select.Parameters.AddWithValue("@session_id", sessionId);
        using var reader = select.ExecuteReader();
        var guests = new List<GuestIdentity>();
        while (reader.Read())
        {
            guests.Add(new GuestIdentity(reader.GetString(0), reader.GetString(1)));
        }

        foreach (var guest in guests)
        {
            this.MarkSessionVisitorAbsentInternal(connection, sessionId, guest, nowUtc);
        }
    }

    private void MarkSessionVisitorAbsentInternal(SqliteConnection connection, long sessionId, GuestIdentity guest, DateTime nowUtc)
    {
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT currently_present, total_seconds, last_visit_start_utc
            FROM session_visitors
            WHERE session_id = @session_id
              AND character_name = @name
              AND home_world = @world;";
        select.Parameters.AddWithValue("@session_id", sessionId);
        select.Parameters.AddWithValue("@name", guest.Name);
        select.Parameters.AddWithValue("@world", guest.HomeWorld);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var currentlyPresent = reader.GetInt32(0) == 1;
        if (!currentlyPresent)
        {
            return;
        }

        var totalSeconds = reader.GetInt64(1);
        var lastVisitStartRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
        var addSeconds = 0L;
        if (lastVisitStartRaw is not null &&
            DateTime.TryParse(lastVisitStartRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startUtc))
        {
            addSeconds = (long)Math.Max(0, (nowUtc - startUtc).TotalSeconds);
        }

        reader.Close();

        using var update = connection.CreateCommand();
        update.CommandText = @"
            UPDATE session_visitors
            SET currently_present = 0,
                total_seconds = @total_seconds,
                last_seen_utc = @now,
                last_visit_start_utc = NULL,
                last_object_id = 0
            WHERE session_id = @session_id
              AND character_name = @name
              AND home_world = @world;";
        update.Parameters.AddWithValue("@total_seconds", totalSeconds + addSeconds);
        update.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("@session_id", sessionId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        _ = update.ExecuteNonQuery();
    }

    private void MarkVisitorAbsentInternal(SqliteConnection connection, long nightId, GuestIdentity guest, DateTime nowUtc)
    {
        using var select = connection.CreateCommand();
        select.CommandText = @"
            SELECT currently_present, total_seconds, last_visit_start_utc
            FROM visitor_night_stats
            WHERE night_id = @night_id
              AND character_name = @name
              AND home_world = @world;";
        select.Parameters.AddWithValue("@night_id", nightId);
        select.Parameters.AddWithValue("@name", guest.Name);
        select.Parameters.AddWithValue("@world", guest.HomeWorld);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var currentlyPresent = reader.GetInt32(0) == 1;
        if (!currentlyPresent)
        {
            return;
        }

        var totalSeconds = reader.GetInt64(1);
        var lastVisitStartRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
        var addSeconds = 0L;
        if (lastVisitStartRaw is not null &&
            DateTime.TryParse(lastVisitStartRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startUtc))
        {
            addSeconds = (long)Math.Max(0, (nowUtc - startUtc).TotalSeconds);
        }

        reader.Close();

        using var update = connection.CreateCommand();
        update.CommandText = @"
            UPDATE visitor_night_stats
            SET currently_present = 0,
                total_seconds = @total_seconds,
                last_seen_utc = @now,
                last_visit_start_utc = NULL,
                last_object_id = 0
            WHERE night_id = @night_id
              AND character_name = @name
              AND home_world = @world;";
        update.Parameters.AddWithValue("@total_seconds", totalSeconds + addSeconds);
        update.Parameters.AddWithValue("@now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("@night_id", nightId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        _ = update.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={this.dbPath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }
}
