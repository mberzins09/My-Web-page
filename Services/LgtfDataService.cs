using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Read-only access to lgtf.sqlite for the Latvia Table Tennis pages.
    ///
    /// DATE FORMAT NOTE
    /// Older competitions store start_date as .NET ticks (long integer).
    /// Newer ones store it as text "yyyy-MM-dd".
    /// NormDate normalises both to yyyy-MM-dd text for comparison and display.
    /// </summary>
    public class LgtfDataService
    {
        private readonly string _cs;

        private const string NormDate = @"
            CASE
                WHEN CAST(c.start_date AS INTEGER) > 10000000000
                THEN date('1970-01-01', '+' || ((CAST(c.start_date AS INTEGER) - 621355968000000000) / 10000000) || ' seconds')
                ELSE c.start_date
            END";

        public LgtfDataService(IConfiguration config)
        {
            _cs = config.GetConnectionString("LgtfConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
        }

        // ── Player search ─────────────────────────────────────────────────────

        public async Task<List<TtPlayer>> SearchPlayersAsync(string query)
        {
            var result = new List<TtPlayer>();
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return result;

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT id, name, surname, birth_date, key_name, Gender
                FROM players
                WHERE name LIKE $q OR surname LIKE $q
                ORDER BY surname, name
                LIMIT 20";
            cmd.Parameters.AddWithValue("$q", $"%{query}%");

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Add(ReadPlayer(r));

            return result;
        }

        public async Task<TtPlayer?> GetPlayerAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, name, surname, birth_date, key_name, Gender FROM players WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);

            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadPlayer(r) : null;
        }

        // ── Tournaments for a player ──────────────────────────────────────────

        public async Task<List<int>> GetYearsForPlayerAsync(int playerId)
        {
            var years = new List<int>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT DISTINCT CAST(strftime('%Y', ({NormDate})) AS INTEGER) AS yr
                FROM   competitions c
                JOIN   games g ON g.competition_id = c.id
                WHERE  g.player1_id = $pid OR g.player2_id = $pid
                ORDER BY yr DESC";
            cmd.Parameters.AddWithValue("$pid", playerId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                years.Add(r.GetInt32(0));

            return years;
        }

        /// <summary>
        /// Returns tournaments for a player, including RatingDiffTotal calculated
        /// by summing RatingCalculator.Calculate across every game in each tournament.
        /// </summary>
        public async Task<List<TtCompetition>> GetTournamentsForPlayerAsync(int playerId, int? year = null)
        {
            var result = new List<TtCompetition>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            // First: get competitions with aggregate game counts and wins
            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT  c.id,
                        c.name,
                        ({NormDate})  AS norm_date,
                        c.places,
                        c.coef,
                        COUNT(g.id)   AS games_count,
                        SUM(CASE
                              WHEN g.player1_id = $pid AND g.player1_sets > g.player2_sets THEN 1
                              WHEN g.player2_id = $pid AND g.player2_sets > g.player1_sets THEN 1
                              ELSE 0 END) AS wins
                FROM    competitions c
                JOIN    games g ON g.competition_id = c.id
                WHERE   (g.player1_id = $pid OR g.player2_id = $pid)
                  AND   ($year IS NULL OR CAST(strftime('%Y', ({NormDate})) AS INTEGER) = $year)
                GROUP BY c.id, c.name, c.start_date, c.places, c.coef
                ORDER BY norm_date DESC";

            cmd.Parameters.AddWithValue("$pid",  playerId);
            cmd.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);

            var competitions = new List<TtCompetition>();
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    int games = r.GetInt32(5);
                    int wins  = r.IsDBNull(6) ? 0 : (int)(long)r.GetValue(6);
                    competitions.Add(new TtCompetition
                    {
                        Id         = r.GetInt32(0),
                        Name       = r.GetString(1),
                        StartDate  = r.IsDBNull(2) ? "" : r.GetString(2),
                        Places     = r.IsDBNull(3) ? "" : r.GetString(3),
                        Coef       = r.GetDouble(4),
                        GamesCount = games,
                        Wins       = wins,
                        Losses     = games - wins
                    });
                }
            }

            // Second: for each competition fetch the per-game points to compute RatingDiffTotal
            foreach (var comp in competitions)
            {
                var gc = con.CreateCommand();
                gc.CommandText = @"
                    SELECT player1_id, player2_id,
                           player1_sets, player2_sets,
                           player1_points, player2_points
                    FROM   games
                    WHERE  competition_id = $cid
                      AND  (player1_id = $pid OR player2_id = $pid)
                      AND  player1_points > 0 AND player2_points > 0";
                gc.Parameters.AddWithValue("$cid", comp.Id);
                gc.Parameters.AddWithValue("$pid", playerId);

                int total = 0;
                await using var gr = await gc.ExecuteReaderAsync();
                while (await gr.ReadAsync())
                {
                    bool isMeP1 = gr.GetInt32(0) == playerId;
                    bool iWin   = isMeP1 ? gr.GetInt32(2) > gr.GetInt32(3)
                                         : gr.GetInt32(3) > gr.GetInt32(2);
                    int myPts   = isMeP1 ? gr.GetInt32(4) : gr.GetInt32(5);
                    int oppPts  = isMeP1 ? gr.GetInt32(5) : gr.GetInt32(4);
                    total += RatingCalculator.Calculate(myPts, oppPts, iWin, comp.Coef);
                }
                comp.RatingDiffTotal = total;
            }

            return competitions;
        }

        public async Task<TtCompetition?> GetCompetitionAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.name, ({NormDate}) AS norm_date, c.places, c.coef
                FROM   competitions c
                WHERE  c.id = $id";
            cmd.Parameters.AddWithValue("$id", id);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new TtCompetition
            {
                Id        = r.GetInt32(0),
                Name      = r.GetString(1),
                StartDate = r.IsDBNull(2) ? "" : r.GetString(2),
                Places    = r.IsDBNull(3) ? "" : r.GetString(3),
                Coef      = r.GetDouble(4)
            };
        }

        // ── Games ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns games for a player in a tournament, each with Coef set so
        /// MyRatingDiff / OpponentRatingDiff can be computed in the view.
        /// </summary>
        public async Task<List<TtGame>> GetGamesForPlayerInTournamentAsync(int playerId, int competitionId)
        {
            var result = new List<TtGame>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT g.id, g.competition_id,
                       g.player1_id, g.player2_id,
                       g.player1_sets, g.player2_sets,
                       g.player1_points, g.player2_points,
                       g.player1_PointsWithBonus, g.player2_PointsWithBonus,
                       g.player1_age, g.player2_age,
                       g.player1_place, g.player2_place,
                       p1.name, p1.surname,
                       p2.name, p2.surname,
                       c.coef
                FROM   games g
                LEFT JOIN players p1 ON p1.id = g.player1_id
                LEFT JOIN players p2 ON p2.id = g.player2_id
                JOIN   competitions c ON c.id = g.competition_id
                WHERE  g.competition_id = $cid
                  AND  (g.player1_id = $pid OR g.player2_id = $pid)";

            cmd.Parameters.AddWithValue("$cid", competitionId);
            cmd.Parameters.AddWithValue("$pid", playerId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Add(MapGame(r, playerId));

            return result;
        }

        public async Task<TtGame?> GetGameDetailAsync(int gameId, int playerId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT g.id, g.competition_id,
                       g.player1_id, g.player2_id,
                       g.player1_sets, g.player2_sets,
                       g.player1_points, g.player2_points,
                       g.player1_PointsWithBonus, g.player2_PointsWithBonus,
                       g.player1_age, g.player2_age,
                       g.player1_place, g.player2_place,
                       p1.name, p1.surname,
                       p2.name, p2.surname,
                       c.coef
                FROM   games g
                LEFT JOIN players p1 ON p1.id = g.player1_id
                LEFT JOIN players p2 ON p2.id = g.player2_id
                JOIN   competitions c ON c.id = g.competition_id
                WHERE  g.id = $id";
            cmd.Parameters.AddWithValue("$id", gameId);

            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapGame(r, playerId) : null;
        }

        public async Task<List<TtH2HGame>> GetH2HGamesAsync(int playerAId, int playerBId)
        {
            var result = new List<TtH2HGame>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT  g.id, g.competition_id,
                        c.name,
                        ({NormDate}) AS norm_date,
                        g.player1_id,
                        g.player1_sets, g.player2_sets,
                        g.player1_points, g.player2_points,
                        c.coef
                FROM    games g
                JOIN    competitions c ON c.id = g.competition_id
                WHERE   (g.player1_id = $a AND g.player2_id = $b)
                   OR   (g.player1_id = $b AND g.player2_id = $a)
                ORDER BY norm_date DESC";
            cmd.Parameters.AddWithValue("$a", playerAId);
            cmd.Parameters.AddWithValue("$b", playerBId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                bool aIsP1  = r.GetInt32(4) == playerAId;
                int  p1Sets = r.GetInt32(5);
                int  p2Sets = r.GetInt32(6);
                int  p1Pts  = r.GetInt32(7);
                int  p2Pts  = r.GetInt32(8);
                double coef = r.GetDouble(9);

                int aSetVal = aIsP1 ? p1Sets : p2Sets;
                int bSetVal = aIsP1 ? p2Sets : p1Sets;
                int aPts    = aIsP1 ? p1Pts  : p2Pts;
                int bPts    = aIsP1 ? p2Pts  : p1Pts;

                bool aWon = aSetVal > bSetVal;
                bool bWon = bSetVal > aSetVal;

                int aRd = (aPts > 0 && bPts > 0) ? RatingCalculator.Calculate(aPts, bPts, aWon, coef) : 0;
                int bRd = (aPts > 0 && bPts > 0) ? RatingCalculator.Calculate(bPts, aPts, bWon, coef) : 0;

                result.Add(new TtH2HGame
                {
                    GameId            = r.GetInt32(0),
                    CompetitionId     = r.GetInt32(1),
                    CompetitionName   = r.GetString(2),
                    Date              = r.IsDBNull(3) ? "" : r.GetString(3),
                    PlayerASets       = aSetVal,
                    PlayerBSets       = bSetVal,
                    PlayerARatingDiff = aRd,
                    PlayerBRatingDiff = bRd,
                });
            }
            return result;
        }

        public async Task<DateTime> GetLastImportedDateAsync()
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT MAX(({NormDate}))
                FROM   competitions c
                WHERE  EXISTS (SELECT 1 FROM games g WHERE g.competition_id = c.id)";

            var val = await cmd.ExecuteScalarAsync();
            if (val == null || val == DBNull.Value) return DateTime.MinValue;
            return DateTime.TryParse(val.ToString(), out var dt) ? dt : DateTime.MinValue;
        }

        public async Task<HashSet<int>> GetAllCompetitionExternalIdsAsync()
        {
            var ids = new HashSet<int>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            try
            {
                var alter = con.CreateCommand();
                alter.CommandText = "ALTER TABLE competitions ADD COLUMN external_event_id INTEGER DEFAULT 0";
                await alter.ExecuteNonQueryAsync();
            }
            catch { /* already exists */ }

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT external_event_id FROM competitions WHERE external_event_id > 0";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                ids.Add(r.GetInt32(0));

            return ids;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static TtPlayer ReadPlayer(SqliteDataReader r) => new()
        {
            Id        = r.GetInt32(0),
            Name      = r.GetString(1),
            Surname   = r.GetString(2),
            BirthDate = r.IsDBNull(3) ? "" : r.GetString(3),
            KeyName   = r.IsDBNull(4) ? "" : r.GetString(4),
            Gender    = r.IsDBNull(5) ? "" : r.GetString(5),
        };

        // Column 18 = c.coef (added at end of SELECT so existing column indices unchanged)
        private static TtGame MapGame(SqliteDataReader r, int myPlayerId)
        {
            int p1Id   = r.GetInt32(2);
            int p2Id   = r.GetInt32(3);
            int p1Sets = r.GetInt32(4);
            int p2Sets = r.GetInt32(5);
            bool isMeP1 = p1Id == myPlayerId;

            var me = new TtGameSide
            {
                PlayerId        = isMeP1 ? p1Id   : p2Id,
                Name            = isMeP1 ? (r.IsDBNull(14) ? "" : r.GetString(14)) : (r.IsDBNull(16) ? "" : r.GetString(16)),
                Surname         = isMeP1 ? (r.IsDBNull(15) ? "" : r.GetString(15)) : (r.IsDBNull(17) ? "" : r.GetString(17)),
                Sets            = isMeP1 ? p1Sets : p2Sets,
                Points          = r.IsDBNull(6)  ? 0 : (isMeP1 ? r.GetInt32(6)  : r.GetInt32(7)),
                PointsWithBonus = r.IsDBNull(8)  ? 0 : (isMeP1 ? r.GetInt32(8)  : r.GetInt32(9)),
                Age             = r.IsDBNull(10) ? 0 : (isMeP1 ? r.GetInt32(10) : r.GetInt32(11)),
                Place           = r.IsDBNull(12) ? 0 : (isMeP1 ? r.GetInt32(12) : r.GetInt32(13)),
                IsWinner        = isMeP1 ? p1Sets > p2Sets : p2Sets > p1Sets,
            };

            var opp = new TtGameSide
            {
                PlayerId        = isMeP1 ? p2Id   : p1Id,
                Name            = isMeP1 ? (r.IsDBNull(16) ? "" : r.GetString(16)) : (r.IsDBNull(14) ? "" : r.GetString(14)),
                Surname         = isMeP1 ? (r.IsDBNull(17) ? "" : r.GetString(17)) : (r.IsDBNull(15) ? "" : r.GetString(15)),
                Sets            = isMeP1 ? p2Sets : p1Sets,
                Points          = r.IsDBNull(6)  ? 0 : (isMeP1 ? r.GetInt32(7)  : r.GetInt32(6)),
                PointsWithBonus = r.IsDBNull(8)  ? 0 : (isMeP1 ? r.GetInt32(9)  : r.GetInt32(8)),
                Age             = r.IsDBNull(10) ? 0 : (isMeP1 ? r.GetInt32(11) : r.GetInt32(10)),
                Place           = r.IsDBNull(12) ? 0 : (isMeP1 ? r.GetInt32(13) : r.GetInt32(12)),
                IsWinner        = isMeP1 ? p2Sets > p1Sets : p1Sets > p2Sets,
            };

            return new TtGame
            {
                Id            = r.GetInt32(0),
                CompetitionId = r.GetInt32(1),
                Coef          = r.IsDBNull(18) ? 0 : r.GetDouble(18),
                Me            = me,
                Opponent      = opp,
            };
        }
    }
}
