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
                        p1.name || ' ' || p1.surname,
                        p2.name || ' ' || p2.surname
                FROM    games g
                JOIN    players p1 ON p1.id = g.player1_id
                JOIN    players p2 ON p2.id = g.player2_id
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
                        p1.name || ' ' || p1.surname,
                        p2.name || ' ' || p2.surname
                FROM    games g
                JOIN    players p1 ON p1.id = g.player1_id
                JOIN    players p2 ON p2.id = g.player2_id
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
            cmd.CommandText = "UPDATE players SET name=$n, surname=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$n",  parts[0]);
            cmd.Parameters.AddWithValue("$s",  parts.Length > 1 ? parts[1] : "");
            cmd.Parameters.AddWithValue("$id", playerId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
