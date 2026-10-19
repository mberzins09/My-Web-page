using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// High scores for the /tetris mini-game. Uses app.db directly with plain SQL, like
    /// SuggestionService/SurveyService, so it needs no EF migration - the table creates itself.
    /// </summary>
    public class TetrisService
    {
        private readonly string _cs;

        public TetrisService(IConfiguration config)
        {
            _cs = config.GetConnectionString("AppConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "app.db")}";
        }

        private static async Task EnsureSchemaAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS TetrisScores (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlayerName      TEXT    NOT NULL,
                    Score           INTEGER NOT NULL,
                    Lines           INTEGER NOT NULL DEFAULT 0,
                    DurationSeconds INTEGER NOT NULL DEFAULT 0,
                    Level           INTEGER NOT NULL DEFAULT 1,
                    CreatedAt       TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_TetrisScores_Score ON TetrisScores (Score DESC);";
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<TetrisScoreEntry>> GetTopScoresAsync(int limit = 10)
        {
            var result = new List<TetrisScoreEntry>();

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Id, PlayerName, Score, Lines, DurationSeconds, Level, CreatedAt FROM TetrisScores ORDER BY Score DESC, Id LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new TetrisScoreEntry
                {
                    Id              = r.GetInt32(0),
                    PlayerName      = r.GetString(1),
                    Score           = r.GetInt32(2),
                    Lines           = r.GetInt32(3),
                    DurationSeconds = r.GetInt32(4),
                    Level           = r.GetInt32(5),
                    CreatedAt       = DateTime.TryParse(r.GetString(6), out var d) ? d : DateTime.MinValue,
                });
            }

            return result;
        }

        /// <summary>Saves a score. Returns the rank it landed at (1-based) among all scores ever saved.</summary>
        public async Task<int> AddScoreAsync(string playerName, int score, int lines, int durationSeconds, int level)
        {
            playerName = (playerName ?? "").Trim();
            if (playerName.Length == 0) playerName = "Anonymous";
            if (playerName.Length > 40) playerName = playerName[..40];
            if (score < 0) score = 0;
            if (durationSeconds < 0) durationSeconds = 0;
            if (level < 1) level = 1;

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var ins = con.CreateCommand();
            ins.CommandText = "INSERT INTO TetrisScores (PlayerName, Score, Lines, DurationSeconds, Level, CreatedAt) VALUES ($n, $s, $l, $dur, $lvl, $d)";
            ins.Parameters.AddWithValue("$n", playerName);
            ins.Parameters.AddWithValue("$s", score);
            ins.Parameters.AddWithValue("$l", lines);
            ins.Parameters.AddWithValue("$dur", durationSeconds);
            ins.Parameters.AddWithValue("$lvl", level);
            ins.Parameters.AddWithValue("$d", DateTime.UtcNow.ToString("o"));
            await ins.ExecuteNonQueryAsync();

            var rankCmd = con.CreateCommand();
            rankCmd.CommandText = "SELECT COUNT(*) + 1 FROM TetrisScores WHERE Score > $s";
            rankCmd.Parameters.AddWithValue("$s", score);
            return Convert.ToInt32(await rankCmd.ExecuteScalarAsync());
        }
    }
}
