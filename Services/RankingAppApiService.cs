using System.Globalization;
using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Read-only access to lgtf.sqlite for the RankingApp mobile app.
    /// Replaces the lgtf.sqlite file that used to be bundled inside the app:
    ///   * GetPlayersAsync          - seeds the app's PlayerDB table on a fresh install
    ///   * GetPlayerHistoryAsync    - all competitions and games of one player ("Get old tournaments")
    /// </summary>
    public class RankingAppApiService
    {
        private readonly string _cs;

        public RankingAppApiService(IConfiguration config)
        {
            _cs = config.GetConnectionString("LgtfConnection") ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
        }

        public async Task<List<RankingAppPlayer>> GetPlayersAsync()
        {
            var result = new List<RankingAppPlayer>();

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, Name, Surname, BirthDate, Gender, Place, OverallPlace,
                       Points, PointsWithBonus, IsActive, NewId, KeyName
                FROM PlayerDB
                ORDER BY Id";

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new RankingAppPlayer
                {
                    Id              = Int(r, 0),
                    Name            = Str(r, 1) ?? "",
                    Surname         = Str(r, 2) ?? "",
                    BirthDate       = Str(r, 3),
                    Gender          = Str(r, 4),
                    Place           = Int(r, 5),
                    OverallPlace    = Int(r, 6),
                    Points          = Int(r, 7),
                    PointsWithBonus = Int(r, 8),
                    IsActive        = Int(r, 9) != 0,
                    NewId           = Int(r, 10),
                    KeyName         = Str(r, 11)
                });
            }

            return result;
        }

        /// <summary>
        /// Looks the player up by KeyName (stable across database rebuilds, unlike Id).
        /// Returns null when no player with that KeyName exists.
        /// </summary>
        public async Task<RankingAppPlayerHistory?> GetPlayerHistoryAsync(string keyName)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var idCmd = con.CreateCommand();
            idCmd.CommandText = "SELECT Id FROM PlayerDB WHERE KeyName = $k LIMIT 1";
            idCmd.Parameters.AddWithValue("$k", keyName);
            var idObj = await idCmd.ExecuteScalarAsync();
            if (idObj is null or DBNull) return null;

            var history = new RankingAppPlayerHistory
            {
                PlayerId = Convert.ToInt32(idObj, CultureInfo.InvariantCulture),
                KeyName  = keyName
            };

            // ── Games (with both players' names) ──────────────────────────────
            var gamesCmd = con.CreateCommand();
            gamesCmd.CommandText = @"
                SELECT g.id, g.competition_id, g.player1_id, g.player2_id,
                       p1.Name, p1.Surname, p2.Name, p2.Surname,
                       g.player1_keyName, g.player2_keyName,
                       g.player1_sets, g.player2_sets,
                       g.player1_points, g.player2_points,
                       g.player1_PointsWithBonus, g.player2_PointsWithBonus,
                       g.player1_age, g.player2_age,
                       g.player1_place, g.player2_place
                FROM games g
                LEFT JOIN PlayerDB p1 ON p1.Id = g.player1_id
                LEFT JOIN PlayerDB p2 ON p2.Id = g.player2_id
                WHERE g.player1_id = $id OR g.player2_id = $id
                ORDER BY g.id";
            gamesCmd.Parameters.AddWithValue("$id", history.PlayerId);

            await using (var r = await gamesCmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    history.Games.Add(new RankingAppGame
                    {
                        Id                     = Int(r, 0),
                        CompetitionId          = Int(r, 1),
                        Player1Id              = Int(r, 2),
                        Player2Id              = Int(r, 3),
                        Player1Name            = Str(r, 4) ?? "",
                        Player1Surname         = Str(r, 5) ?? "",
                        Player2Name            = Str(r, 6) ?? "",
                        Player2Surname         = Str(r, 7) ?? "",
                        Player1KeyName         = Str(r, 8) ?? "",
                        Player2KeyName         = Str(r, 9) ?? "",
                        Player1Sets            = Int(r, 10),
                        Player2Sets            = Int(r, 11),
                        Player1Points          = Int(r, 12),
                        Player2Points          = Int(r, 13),
                        Player1PointsWithBonus = Int(r, 14),
                        Player2PointsWithBonus = Int(r, 15),
                        Player1Age             = Int(r, 16),
                        Player2Age             = Int(r, 17),
                        Player1Place           = Int(r, 18),
                        Player2Place           = Int(r, 19)
                    });
                }
            }

            // ── Competitions the player took part in ──────────────────────────
            var compCmd = con.CreateCommand();
            compCmd.CommandText = @"
                SELECT id, name, start_date, places, coef
                FROM competitions
                WHERE id IN (SELECT DISTINCT competition_id FROM games
                             WHERE player1_id = $id OR player2_id = $id)
                ORDER BY start_date";
            compCmd.Parameters.AddWithValue("$id", history.PlayerId);

            await using (var r = await compCmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    history.Competitions.Add(new RankingAppCompetition
                    {
                        Id        = Int(r, 0),
                        Name      = Str(r, 1) ?? "",
                        StartDate = Str(r, 2) ?? "",
                        Places    = Str(r, 3),
                        Coef      = r.IsDBNull(4) ? 0 : Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)
                    });
                }
            }

            return history;
        }

        // SQLite columns are loosely typed (an INTEGER column may hold text), so convert defensively.
        private static int Int(SqliteDataReader r, int i)
        {
            if (r.IsDBNull(i)) return 0;
            var v = r.GetValue(i);
            return v switch
            {
                long l   => (int)l,
                double d => (int)d,
                string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                _        => Convert.ToInt32(v, CultureInfo.InvariantCulture)
            };
        }

        private static string? Str(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
    }
}
