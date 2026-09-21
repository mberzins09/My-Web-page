using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Admin read/write access to lgtf.sqlite.
    ///
    /// DATE FORMAT NOTE
    /// Older competitions (MAUI-imported) store start_date as .NET ticks (long integer).
    /// Newer ones (LgtfImportService) store it as text "yyyy-MM-dd".
    /// NormDate normalises both to yyyy-MM-dd text for comparison and display.
    /// Detection: CAST(start_date AS INTEGER) > 10_000_000_000 → ticks, else text.
    /// Conversion: date('1970-01-01', '+' || ((ticks - 621355968000000000) / 10000000) || ' seconds')
    /// </summary>
    public class LgtfAdminService
    {
        private readonly string _cs;

        // Normalises c.start_date to yyyy-MM-dd text regardless of storage format.
        private const string NormDate = @"
            CASE
                WHEN CAST(c.start_date AS INTEGER) > 10000000000
                THEN date('1970-01-01', '+' || ((CAST(c.start_date AS INTEGER) - 621355968000000000) / 10000000) || ' seconds')
                ELSE c.start_date
            END";

        public LgtfAdminService(IConfiguration config)
        {
            _cs = config.GetConnectionString("LgtfConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
        }

        // ====================================================================
        //  Database clean-up
        // ====================================================================

        /// <summary>
        /// One-off maintenance for lgtf.sqlite:
        ///   1. Makes a backup copy of the database file (VACUUM INTO).
        ///   2. Removes duplicate PlayerDB rows (same KeyName). Keeper = has Gender,
        ///      then IsActive = 1, then lowest Id. Games pointing at a removed row
        ///      are re-pointed to the keeper.
        ///   3. Re-points games.player1_id / player2_id to PlayerDB.Id by key name,
        ///      so games use the PlayerDB id space only (needed once "players" is gone).
        ///   4. Adds a UNIQUE index on PlayerDB.KeyName so duplicates cannot come back.
        ///   5. Drops the legacy "players" table.
        /// Steps 2-5 run in a single transaction - either everything happens or nothing.
        /// </summary>
        public async Task<CleanDatabaseResult> CleanDatabaseAsync()
        {
            var result = new CleanDatabaseResult();

            // ── 1. Backup ────────────────────────────────────────────────────
            var csb    = new SqliteConnectionStringBuilder(_cs);
            string dbPath = Path.GetFullPath(csb.DataSource);
            string backupPath = Path.Combine(
                Path.GetDirectoryName(dbPath)!,
                $"{Path.GetFileNameWithoutExtension(dbPath)}.backup-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(dbPath)}");

            await using (var bcon = new SqliteConnection(_cs))
            {
                await bcon.OpenAsync();
                var bcmd = bcon.CreateCommand();
                bcmd.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}'";
                await bcmd.ExecuteNonQueryAsync();
            }
            result.BackupFile = backupPath;

            // ── 2-5. Clean-up in one transaction ─────────────────────────────
            await using (var con = new SqliteConnection(_cs))
            {
                await con.OpenAsync();
                await using var tr = (SqliteTransaction)await con.BeginTransactionAsync();

                async Task<int> Exec(string sql)
                {
                    var c = con.CreateCommand();
                    c.Transaction = tr;
                    c.CommandText = sql;
                    return await c.ExecuteNonQueryAsync();
                }

                async Task<int> Scalar(string sql)
                {
                    var c = con.CreateCommand();
                    c.Transaction = tr;
                    c.CommandText = sql;
                    return Convert.ToInt32(await c.ExecuteScalarAsync());
                }

                // Old id -> keeper id for every duplicate row
                await Exec("DROP TABLE IF EXISTS temp._dup_map");
                await Exec("CREATE TEMP TABLE _dup_map (OldId INTEGER PRIMARY KEY, KeeperId INTEGER NOT NULL)");
                await Exec(@"
                    INSERT INTO _dup_map (OldId, KeeperId)
                    SELECT OldId, KeeperId
                    FROM (
                        SELECT Id AS OldId,
                               FIRST_VALUE(Id) OVER (
                                   PARTITION BY KeyName
                                   ORDER BY CASE WHEN Gender IS NOT NULL AND Gender != '' THEN 0 ELSE 1 END,
                                            CASE WHEN IsActive = 1 THEN 0 ELSE 1 END,
                                            Id) AS KeeperId
                        FROM PlayerDB
                        WHERE KeyName IS NOT NULL AND KeyName != ''
                    )
                    WHERE OldId != KeeperId");

                result.DuplicatesRemoved = await Scalar("SELECT COUNT(*) FROM _dup_map");
                result.DuplicateGroups   = await Scalar("SELECT COUNT(DISTINCT KeeperId) FROM _dup_map");

                // Games that point at a duplicate -> point at the keeper
                result.GamesRepointed += await Exec(@"
                    UPDATE games
                    SET    player1_id = (SELECT KeeperId FROM _dup_map WHERE OldId = games.player1_id)
                    WHERE  player1_id IN (SELECT OldId FROM _dup_map)");
                result.GamesRepointed += await Exec(@"
                    UPDATE games
                    SET    player2_id = (SELECT KeeperId FROM _dup_map WHERE OldId = games.player2_id)
                    WHERE  player2_id IN (SELECT OldId FROM _dup_map)");

                await Exec("DELETE FROM PlayerDB WHERE Id IN (SELECT OldId FROM _dup_map)");
                await Exec("DROP TABLE temp._dup_map");

                // Make games.player*_id agree with PlayerDB.Id (matched by key name)
                foreach (var side in new[] { "player1", "player2" })
                {
                    result.GamesResynced += await Exec($@"
                        UPDATE games
                        SET    {side}_id = (SELECT p.Id FROM PlayerDB p WHERE p.KeyName = games.{side}_keyName)
                        WHERE  {side}_keyName IS NOT NULL AND {side}_keyName != ''
                          AND  EXISTS (SELECT 1 FROM PlayerDB p WHERE p.KeyName = games.{side}_keyName)
                          AND  {side}_id IS NOT (SELECT p.Id FROM PlayerDB p WHERE p.KeyName = games.{side}_keyName)");
                }

                // No more duplicates, ever
                await Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_PlayerDB_KeyName
                             ON PlayerDB (KeyName)
                             WHERE KeyName IS NOT NULL AND KeyName != ''");

                // Legacy table
                await Exec("DROP TABLE IF EXISTS players");
                result.PlayersTableDropped = true;

                await tr.CommitAsync();
            }

            // ── Shrink the file (cannot run inside a transaction) ────────────
            try
            {
                await using var vcon = new SqliteConnection(_cs);
                await vcon.OpenAsync();
                var vcmd = vcon.CreateCommand();
                vcmd.CommandText = "VACUUM";
                await vcmd.ExecuteNonQueryAsync();
                result.Vacuumed = true;
            }
            catch { /* not critical */ }

            return result;
        }

        // ====================================================================
        //  Tournaments
        // ====================================================================

        public async Task<List<AdminTournamentVm>> GetTournamentsByYearMonthAsync(int year, int month)
        {
            var result = new List<AdminTournamentVm>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT  c.id,
                        c.name,
                        ({NormDate})                        AS norm_date,
                        COALESCE(c.coef, 0),
                        COALESCE(c.event_type, 'singles'),
                        COALESCE(c.places, ''),
                        COUNT(g.id)                         AS game_count
                FROM    competitions c
                LEFT JOIN games g ON g.competition_id = c.id
                WHERE   CAST(strftime('%Y', ({NormDate})) AS INTEGER) = $year
                  AND   CAST(strftime('%m', ({NormDate})) AS INTEGER) = $month
                GROUP BY c.id, c.name, c.start_date, c.coef, c.event_type, c.places
                ORDER BY norm_date DESC, c.name";

            cmd.Parameters.AddWithValue("$year",  year);
            cmd.Parameters.AddWithValue("$month", month);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new AdminTournamentVm
                {
                    Id        = r.GetInt32(0),
                    Name      = r.GetString(1),
                    Date      = r.IsDBNull(2) ? "" : r.GetString(2),
                    Coef      = r.IsDBNull(3) ? 0  : r.GetDouble(3),
                    EventType = r.IsDBNull(4) ? "" : r.GetString(4),
                    Places    = r.IsDBNull(5) ? "" : r.GetString(5),
                    GameCount = r.GetInt32(6),
                });
            }

            return result;
        }

        public async Task<AdminTournamentVm?> GetTournamentAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT  c.id,
                        c.name,
                        ({NormDate})          AS norm_date,
                        COALESCE(c.coef, 0),
                        COALESCE(c.event_type, 'singles'),
                        COALESCE(c.places, ''),
                        COUNT(g.id)           AS game_count
                FROM    competitions c
                LEFT JOIN games g ON g.competition_id = c.id
                WHERE   c.id = $id
                GROUP BY c.id, c.name, c.start_date, c.coef, c.event_type, c.places";
            cmd.Parameters.AddWithValue("$id", id);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;

            return new AdminTournamentVm
            {
                Id        = r.GetInt32(0),
                Name      = r.GetString(1),
                Date      = r.IsDBNull(2) ? "" : r.GetString(2),
                Coef      = r.IsDBNull(3) ? 0  : r.GetDouble(3),
                EventType = r.IsDBNull(4) ? "" : r.GetString(4),
                Places    = r.IsDBNull(5) ? "" : r.GetString(5),
                GameCount = r.GetInt32(6),
            };
        }

        public async Task UpdateTournamentAsync(int id, string name, string date, double coef, string places)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                UPDATE competitions
                SET    name = $n, start_date = $d, coef = $c, places = $p
                WHERE  id = $id";
            cmd.Parameters.AddWithValue("$n",  name.Trim());
            cmd.Parameters.AddWithValue("$d",  date);
            cmd.Parameters.AddWithValue("$c",  coef);
            cmd.Parameters.AddWithValue("$p",  places.Trim());
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Deletes tournament + all its games. Returns number of games deleted.</summary>
        public async Task<int> DeleteTournamentAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await using var tr = await con.BeginTransactionAsync();

            var dg = con.CreateCommand(); dg.Transaction = (SqliteTransaction)tr;
            dg.CommandText = "DELETE FROM games WHERE competition_id = $id";
            dg.Parameters.AddWithValue("$id", id);
            int gamesDeleted = await dg.ExecuteNonQueryAsync();

            var dc = con.CreateCommand(); dc.Transaction = (SqliteTransaction)tr;
            dc.CommandText = "DELETE FROM competitions WHERE id = $id";
            dc.Parameters.AddWithValue("$id", id);
            await dc.ExecuteNonQueryAsync();

            await tr.CommitAsync();
            return gamesDeleted;
        }

        // ====================================================================
        //  Games
        // ====================================================================

        public async Task<List<AdminGameVm>> GetGamesForTournamentAsync(int competitionId)
        {
            var result = new List<AdminGameVm>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT  g.id,
                        g.player1_id, g.player2_id,
                        g.player1_sets, g.player2_sets,
                        TRIM(COALESCE(p1.Name, '') || ' ' || COALESCE(p1.Surname, '')),
                        TRIM(COALESCE(p2.Name, '') || ' ' || COALESCE(p2.Surname, ''))
                FROM    games g
                LEFT JOIN PlayerDB p1 ON p1.Id = g.player1_id
                LEFT JOIN PlayerDB p2 ON p2.Id = g.player2_id
                WHERE   g.competition_id = $cid
                ORDER BY g.id";
            cmd.Parameters.AddWithValue("$cid", competitionId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new AdminGameVm
                {
                    Id     = r.GetInt32(0),
                    P1Id   = r.GetInt32(1),
                    P2Id   = r.GetInt32(2),
                    S1     = r.GetInt32(3),
                    S2     = r.GetInt32(4),
                    P1Name = r.IsDBNull(5) ? "" : r.GetString(5),
                    P2Name = r.IsDBNull(6) ? "" : r.GetString(6),
                });
            }

            return result;
        }

        public async Task<AdminGameVm?> GetGameAsync(int gameId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT  g.id,
                        g.player1_id, g.player2_id,
                        g.player1_sets, g.player2_sets,
                        TRIM(COALESCE(p1.Name, '') || ' ' || COALESCE(p1.Surname, '')),
                        TRIM(COALESCE(p2.Name, '') || ' ' || COALESCE(p2.Surname, ''))
                FROM    games g
                LEFT JOIN PlayerDB p1 ON p1.Id = g.player1_id
                LEFT JOIN PlayerDB p2 ON p2.Id = g.player2_id
                WHERE   g.id = $id";
            cmd.Parameters.AddWithValue("$id", gameId);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;

            return new AdminGameVm
            {
                Id     = r.GetInt32(0),
                P1Id   = r.GetInt32(1),
                P2Id   = r.GetInt32(2),
                S1     = r.GetInt32(3),
                S2     = r.GetInt32(4),
                P1Name = r.IsDBNull(5) ? "" : r.GetString(5),
                P2Name = r.IsDBNull(6) ? "" : r.GetString(6),
            };
        }

        public async Task UpdateGameAsync(int gameId, int s1, int s2, int p1Id, string p1Name, int p2Id, string p2Name)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE games SET player1_sets=$s1, player2_sets=$s2 WHERE id=$id";
            cmd.Parameters.AddWithValue("$s1", s1);
            cmd.Parameters.AddWithValue("$s2", s2);
            cmd.Parameters.AddWithValue("$id", gameId);
            await cmd.ExecuteNonQueryAsync();

            await UpdatePlayerNameAsync(con, p1Id, p1Name);
            await UpdatePlayerNameAsync(con, p2Id, p2Name);
        }

        /// <summary>Deletes a single game. Returns competition_id so caller can navigate back.</summary>
        public async Task<int> DeleteGameAsync(int gameId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            // Get competition_id before deleting
            var sel = con.CreateCommand();
            sel.CommandText = "SELECT competition_id FROM games WHERE id=$id";
            sel.Parameters.AddWithValue("$id", gameId);
            int compId = Convert.ToInt32(await sel.ExecuteScalarAsync() ?? 0);

            var del = con.CreateCommand();
            del.CommandText = "DELETE FROM games WHERE id=$id";
            del.Parameters.AddWithValue("$id", gameId);
            await del.ExecuteNonQueryAsync();

            return compId;
        }

        public async Task<int> GetCompetitionIdForGameAsync(int gameId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT competition_id FROM games WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", gameId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        }

        private static async Task UpdatePlayerNameAsync(SqliteConnection con, int playerId, string fullName)
        {
            var parts = fullName.Trim().Split(' ', 2);
            var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE PlayerDB SET Name=$n, Surname=$s WHERE Id=$id";
            cmd.Parameters.AddWithValue("$n",  parts[0]);
            cmd.Parameters.AddWithValue("$s",  parts.Length > 1 ? parts[1] : "");
            cmd.Parameters.AddWithValue("$id", playerId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
