using Microsoft.Data.Sqlite;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// A second, independent table tennis ranking ("alternative ranking"), ported from a desktop
    /// tool (RankingEngine2) the admin built separately. It uses the same PlayerDB table as the
    /// official LGTF ranking for a player's id/name/surname/gender, and the same competitions/games
    /// tables for results, but its own Elo-style points - it never syncs against the official API and
    /// never reads or writes PlayerDB.Points/PointsWithBonus, so it can't disturb the official ranking.
    ///
    /// Unlike the official ranking (which re-anchors to the API every month and so can recalculate one
    /// month at a time), this engine has no outside anchor: a player's rating is only ever derived by
    /// replaying every competition in order from the very first one. So RunFullAsync always replays the
    /// complete history - there is no "just this month" mode, by design, matching the original tool.
    /// Results are written to rankings2, created in lgtf.sqlite itself (not a separate database file).
    /// A rerun UPSERTs by (player_id, month) rather than inserting, so importing more than once in the
    /// same month updates that month's row instead of duplicating it.
    /// </summary>
    public class AltRankingService
    {
        private readonly string _cs;

        public AltRankingService(IConfiguration config)
        {
            _cs = config.GetConnectionString("LgtfConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
        }

        private static class Elo
        {
            public const int BaseK = 40;

            public static double Expected(int ra, int rb) => 1.0 / (1.0 + Math.Pow(10, (rb - ra) / 400.0));

            public static int NewRating(int rating, double score, double expected, double coef) =>
                (int)Math.Round(rating + BaseK * coef * (score - expected));
        }

        // ── Public entry points ─────────────────────────────────────────────

        /// <summary>
        /// Full rebuild: wipes the saved state and replays every competition from the very start.
        /// For first-time setup, or as a manual "rebuild everything" button. Running this a second
        /// time always drops every alternative-ranking table and redoes everything from scratch -
        /// that's by design, not a bug.
        /// </summary>
        public Task RunFullAsync(Action<string>? progress = null) => RunCoreAsync(reset: true, progress);

        /// <summary>
        /// The normal path, called after every import: picks up the saved player state from the
        /// last run and only processes competitions that haven't been counted yet, instead of
        /// replaying the whole history. Only the month(s) that actually received a new competition
        /// get their rankings2 snapshot touched - everything earlier is left exactly as it was.
        ///
        /// A competition dated earlier than ones already processed (a late-arriving historical
        /// result - which happens routinely, since one organizer is reliably slow to enter results)
        /// is treated like any other new competition and folded in incrementally, in whatever order
        /// it was imported. This ranking doesn't need perfect chronological precision: an
        /// out-of-order competition essentially never shares players with ones already counted, so
        /// there's no automatic full-rebuild fallback here. Use RunFullAsync explicitly if a true
        /// from-scratch recompute is ever wanted.
        /// </summary>
        public Task RunIncrementalAsync(Action<string>? progress = null) => RunCoreAsync(reset: false, progress);

        private async Task RunCoreAsync(bool reset, Action<string>? progress)
        {
            // Blazor Server only flushes a queued StateHasChanged render if the calling method
            // actually yields control at some point; SQLite calls below can resolve near-
            // synchronously, so without an explicit yield here the admin page's log can sit blank
            // for a long time and then dump everything at once. Report() fixes that.
            async Task Report(string msg)
            {
                progress?.Invoke(msg);
                await Task.Yield();
            }

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            if (reset)
            {
                await Report("Alternative ranking: full rebuild - clearing saved state...");
                await ResetAsync(con);
            }

            await Report("Alternative ranking: loading competitions, games, and player genders...");
            var byMonth = await LoadCompetitionsByMonthAsync(con);
            var gamesByCompetition = await LoadGamesByCompetitionAsync(con);
            var genderByPlayer = await LoadGendersAsync(con);

            int totalComps = byMonth.Sum(m => m.Comps.Count);
            int totalGames = gamesByCompetition.Values.Sum(g => g.Count);
            await Report($"Loaded {totalComps} competition(s), {totalGames} game(s), across {byMonth.Count} month(s).");

            var processedIds = reset ? [] : await LoadProcessedCompetitionIdsAsync(con);
            var rating = reset ? new Dictionary<int, AltPRating>() : await LoadStateAsync(con);

            int totalNew = byMonth.Sum(m => m.Comps.Count(c => !processedIds.Contains(c.Id)));
            await Report(totalNew == 0
                ? "Nothing new to process."
                : $"{totalNew} competition(s) to process ({(reset ? "full rebuild" : "incremental")})...");

            int monthsTouched = 0;
            int compsProcessed = 0;

            await using var tr = (SqliteTransaction)await con.BeginTransactionAsync();

            foreach (var (monthKey, comps) in byMonth)
            {
                var newComps = comps.Where(c => !processedIds.Contains(c.Id)).ToList();
                if (newComps.Count == 0) continue;   // nothing new here - leave this month's rankings2 row untouched

                foreach (var comp in newComps)
                {
                    var games = gamesByCompetition.GetValueOrDefault(comp.Id) ?? [];
                    var (tournamentDeltas, gameDeltas) = CalculateCompetitionAdvanced(comp, games, rating, genderByPlayer);
                    await SaveTournamentResultsAsync(con, tr, tournamentDeltas);
                    await SaveGameResultsAsync(con, tr, gameDeltas);
                    processedIds.Add(comp.Id);
                    compsProcessed++;
                    await Report($"  [{compsProcessed}/{totalNew}] {comp.StartDate:yyyy-MM-dd} (competition #{comp.Id}, {games.Count} game(s))");
                }

                await SaveMonthAsync(con, tr, monthKey, rating);
                monthsTouched++;
                await Report($"✓ {monthKey} saved - {rating.Count} players tracked so far.");
            }

            await SaveStateAsync(con, tr, rating);
            await SaveProcessedCompetitionIdsAsync(con, tr, processedIds);

            await tr.CommitAsync();

            await Report(compsProcessed == 0
                ? "Alternative ranking: nothing new - already up to date."
                : $"Alternative ranking: done, {compsProcessed} new competition(s) across {monthsTouched} month(s).");
        }

        // ── Reading, for display ────────────────────────────────────────────

        /// <summary>Every year/month that has saved standings, newest first - for the filter dropdowns.</summary>
        public async Task<List<string>> GetAvailableMonthsAsync()
        {
            var result = new List<string>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT month FROM rankings2 ORDER BY month DESC";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Add(r.GetString(0));

            return result;
        }

        /// <summary>
        /// Standings for one "yyyy-MM" month, joined with PlayerDB for name/surname/gender. Mirrors
        /// GetPlayersPageAsync's filters. Pass null for month to get the most recent one available.
        /// </summary>
        public async Task<(List<AltPlayerRankingRow> Players, int Total, string? Month)> GetCurrentPageAsync(
            string? gender, bool showInactive, int page, int pageSize, string? search, string? month = null)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await EnsureSchemaAsync(con);

            if (string.IsNullOrWhiteSpace(month))
            {
                var monthCmd = con.CreateCommand();
                monthCmd.CommandText = "SELECT MAX(month) FROM rankings2";
                var monthObj = await monthCmd.ExecuteScalarAsync();
                month = monthObj as string;
            }
            if (month == null) return ([], 0, null);

            // Ranking conditions scope which rows get numbered at all (the month, active/inactive,
            // and gender filters) - "Place" is computed from these alone, via ROW_NUMBER() below, so
            // that a player's place is their true place within that ranking, not their position
            // among whatever a text search happens to match. The search condition is applied
            // afterwards, in an outer WHERE, so it narrows the rows shown without renumbering them.
            var rankingConditions = new List<string> { "r.month = $month" };
            if (!showInactive) rankingConditions.Add("r.is_active = 1");
            if (!string.IsNullOrWhiteSpace(gender) && gender != "all")
            {
                if (gender == "foreigns")
                    rankingConditions.Add("p.Gender IS NULL");
                else if (gender == "alllatvians")
                    rankingConditions.Add("p.Gender IS NOT NULL");
                else
                    rankingConditions.Add("p.Gender = $gender");
            }
            string rankingWhere = string.Join(" AND ", rankingConditions);

            var searchConditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
                searchConditions.Add("(ranked.Name LIKE $search OR ranked.Surname LIKE $search)");
            string searchWhere = searchConditions.Count > 0 ? "WHERE " + string.Join(" AND ", searchConditions) : "";

            string rankedCte = $@"
                WITH ranked AS (
                    SELECT p.Id AS Id, COALESCE(p.Name,'') AS Name, COALESCE(p.Surname,'') AS Surname,
                           p.Gender AS Gender, r.points AS Points, r.is_active AS IsActive,
                           ROW_NUMBER() OVER (ORDER BY r.points DESC, p.Id) AS Place
                    FROM rankings2 r
                    JOIN PlayerDB p ON p.Id = r.player_id
                    WHERE {rankingWhere}
                )";

            var countCmd = con.CreateCommand();
            countCmd.CommandText = $@"
                {rankedCte}
                SELECT COUNT(*) FROM ranked {searchWhere}";
            AddFilterParams(countCmd, month, gender, search);
            int total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                {rankedCte}
                SELECT Id, Name, Surname, Gender, Points, IsActive, Place
                FROM ranked
                {searchWhere}
                ORDER BY Points DESC, Id
                LIMIT $limit OFFSET $offset";
            AddFilterParams(cmd, month, gender, search);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", page * pageSize);

            var result = new List<AltPlayerRankingRow>();
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    result.Add(new AltPlayerRankingRow
                    {
                        PlayerId = r.GetInt32(0),
                        Name     = r.GetString(1),
                        Surname  = r.GetString(2),
                        Gender   = r.IsDBNull(3) ? null : r.GetString(3),
                        Points   = r.GetInt32(4),
                        IsActive = r.GetInt32(5) == 1,
                        Place    = r.GetInt32(6),
                    });
                }
            }

            return (result, total, month);
        }

        // Same "either a text date or a .NET ticks value" normalisation LgtfDataService uses, kept
        // identical so dates read the same across both rankings.
        private const string NormDate = @"
            CASE
                WHEN CAST(c.start_date AS INTEGER) > 10000000000
                THEN date('1970-01-01', '+' || ((CAST(c.start_date AS INTEGER) - 621355968000000000) / 10000000) || ' seconds')
                ELSE c.start_date
            END";

        public async Task<AltTtPlayer?> GetPlayerAsync(int id)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Id, COALESCE(Name,''), COALESCE(Surname,''), Gender FROM PlayerDB WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new AltTtPlayer
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                Surname = r.GetString(2),
                Gender = r.IsDBNull(3) ? null : r.GetString(3),
            };
        }

        public async Task<List<int>> GetYearsForPlayerAsync(int playerId)
        {
            var years = new List<int>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT DISTINCT CAST(strftime('%Y', ({NormDate})) AS INTEGER) AS yr
                FROM   rankings2_tournament_results t
                JOIN   competitions c ON c.id = t.competition_id
                WHERE  t.player_id = $pid
                ORDER BY yr DESC";
            cmd.Parameters.AddWithValue("$pid", playerId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                years.Add(r.GetInt32(0));

            return years;
        }

        /// <summary>Every tournament this player has an alternative-ranking record for, newest first.</summary>
        public async Task<List<AltTtCompetition>> GetTournamentsForPlayerAsync(int playerId, int? year = null)
        {
            var result = new List<AltTtCompetition>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.name, ({NormDate}) AS norm_date, c.coef,
                       t.points_before, t.correction, t.is_first_tournament, t.initial_rating, t.points_after,
                       (SELECT COUNT(*) FROM games gg
                            WHERE gg.competition_id = c.id AND (gg.player1_id = $pid OR gg.player2_id = $pid)) AS games_count,
                       (SELECT COUNT(*) FROM games gg
                            WHERE gg.competition_id = c.id
                              AND ((gg.player1_id = $pid AND gg.player1_sets > gg.player2_sets)
                                OR (gg.player2_id = $pid AND gg.player2_sets > gg.player1_sets))) AS wins
                FROM   rankings2_tournament_results t
                JOIN   competitions c ON c.id = t.competition_id
                WHERE  t.player_id = $pid
                  AND  ($year IS NULL OR CAST(strftime('%Y', ({NormDate})) AS INTEGER) = $year)
                ORDER BY norm_date DESC";
            cmd.Parameters.AddWithValue("$pid", playerId);
            cmd.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                int games = r.GetInt32(9);
                int wins = r.GetInt32(10);
                result.Add(new AltTtCompetition
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    Date = DateTime.TryParse(r.IsDBNull(2) ? null : r.GetString(2), out var dt) ? dt : DateTime.MinValue,
                    Coef = r.GetDouble(3),
                    PointsBefore = r.GetInt32(4),
                    Correction = r.IsDBNull(5) ? null : r.GetInt32(5),
                    IsFirstTournament = r.GetInt32(6) == 1,
                    InitialRating = r.IsDBNull(7) ? null : r.GetInt32(7),
                    PointsAfter = r.GetInt32(8),
                    GamesCount = games,
                    Wins = wins,
                    Losses = games - wins,
                });
            }

            return result;
        }

        /// <summary>Win/loss aren't tracked in RatingDiff terms here - a win is simply points_after > points_before for that game.</summary>
        public async Task<(string Name, DateTime Date, double Coef)?> GetCompetitionHeaderAsync(int competitionId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.name, ({NormDate}) AS norm_date, c.coef
                FROM competitions c WHERE c.id = $id";
            cmd.Parameters.AddWithValue("$id", competitionId);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return (r.GetString(0), DateTime.TryParse(r.IsDBNull(1) ? null : r.GetString(1), out var dt) ? dt : DateTime.MinValue, r.GetDouble(2));
        }

        /// <summary>The same tournament-level record GetTournamentsForPlayerAsync returns, for one specific tournament.</summary>
        public async Task<AltTtCompetition?> GetTournamentForPlayerAsync(int playerId, int competitionId)
        {
            var list = await GetTournamentsForPlayerAsync(playerId);
            return list.FirstOrDefault(c => c.Id == competitionId);
        }

        public async Task<List<AltTtGame>> GetGamesForPlayerInTournamentAsync(int playerId, int competitionId)
        {
            var result = new List<AltTtGame>();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT g.id, g.competition_id, g.player1_id, g.player2_id, g.player1_sets, g.player2_sets,
                       p1.Name, p1.Surname, p2.Name, p2.Surname,
                       r.points_before, r.points_after, r.opponent_id,
                       ro.points_before, ro.points_after
                FROM   games g
                LEFT JOIN PlayerDB p1 ON p1.Id = g.player1_id
                LEFT JOIN PlayerDB p2 ON p2.Id = g.player2_id
                JOIN   rankings2_game_results r  ON r.game_id = g.id AND r.player_id = $pid
                LEFT JOIN rankings2_game_results ro ON ro.game_id = g.id AND ro.player_id = r.opponent_id
                WHERE  g.competition_id = $cid AND (g.player1_id = $pid OR g.player2_id = $pid)";
            cmd.Parameters.AddWithValue("$cid", competitionId);
            cmd.Parameters.AddWithValue("$pid", playerId);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Add(MapGame(r, playerId));

            return result;
        }

        public async Task<AltTtGame?> GetGameDetailAsync(int gameId, int playerId)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT g.id, g.competition_id, g.player1_id, g.player2_id, g.player1_sets, g.player2_sets,
                       p1.Name, p1.Surname, p2.Name, p2.Surname,
                       r.points_before, r.points_after, r.opponent_id,
                       ro.points_before, ro.points_after
                FROM   games g
                LEFT JOIN PlayerDB p1 ON p1.Id = g.player1_id
                LEFT JOIN PlayerDB p2 ON p2.Id = g.player2_id
                JOIN   rankings2_game_results r  ON r.game_id = g.id AND r.player_id = $pid
                LEFT JOIN rankings2_game_results ro ON ro.game_id = g.id AND ro.player_id = r.opponent_id
                WHERE  g.id = $id";
            cmd.Parameters.AddWithValue("$id", gameId);
            cmd.Parameters.AddWithValue("$pid", playerId);

            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapGame(r, playerId) : null;
        }

        private static AltTtGame MapGame(SqliteDataReader r, int playerId)
        {
            int p1Id = r.GetInt32(2);
            bool isMeP1 = p1Id == playerId;

            var me = new AltTtGameSide
            {
                PlayerId = playerId,
                Name = isMeP1 ? (r.IsDBNull(6) ? "" : r.GetString(6)) : (r.IsDBNull(8) ? "" : r.GetString(8)),
                Surname = isMeP1 ? (r.IsDBNull(7) ? "" : r.GetString(7)) : (r.IsDBNull(9) ? "" : r.GetString(9)),
                Sets = isMeP1 ? r.GetInt32(4) : r.GetInt32(5),
                PointsBefore = r.GetInt32(10),
                PointsAfter = r.GetInt32(11),
            };
            me.IsWinner = me.Sets > (isMeP1 ? r.GetInt32(5) : r.GetInt32(4));

            int oppId = r.GetInt32(12);
            var opp = new AltTtGameSide
            {
                PlayerId = oppId,
                Name = isMeP1 ? (r.IsDBNull(8) ? "" : r.GetString(8)) : (r.IsDBNull(6) ? "" : r.GetString(6)),
                Surname = isMeP1 ? (r.IsDBNull(9) ? "" : r.GetString(9)) : (r.IsDBNull(7) ? "" : r.GetString(7)),
                Sets = isMeP1 ? r.GetInt32(5) : r.GetInt32(4),
                IsWinner = !me.IsWinner,
                PointsBefore = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                PointsAfter  = r.IsDBNull(14) ? 0 : r.GetInt32(14),
            };

            return new AltTtGame
            {
                Id = r.GetInt32(0),
                CompetitionId = r.GetInt32(1),
                Me = me,
                Opponent = opp,
            };
        }

        private static void AddFilterParams(SqliteCommand cmd, string month, string? gender, string? search)
        {
            cmd.Parameters.AddWithValue("$month", month);

            if (!string.IsNullOrWhiteSpace(gender) &&
                gender != "all" &&
                gender != "foreigns" &&
                gender != "alllatvians")
            {
                cmd.Parameters.AddWithValue("$gender", gender);
            }

            if (!string.IsNullOrWhiteSpace(search))
                cmd.Parameters.AddWithValue("$search", $"%{search}%");
        }

        // ── Schema ───────────────────────────────────────────────────────────

        private static async Task EnsureSchemaAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS rankings2 (
                    player_id INTEGER NOT NULL,
                    month     TEXT    NOT NULL,
                    points    INTEGER NOT NULL,
                    is_active INTEGER NOT NULL,
                    PRIMARY KEY (player_id, month)
                );

                -- The engine's full working state as of the last processed competition - needed to
                -- resume correctly (points alone isn't enough: Competitions/GamesVsMale/GamesVsFemale
                -- all feed back into the coefficients for the *next* competition processed).
                CREATE TABLE IF NOT EXISTS rankings2_state (
                    player_id              INTEGER PRIMARY KEY,
                    points                 INTEGER NOT NULL,
                    games_played           INTEGER NOT NULL,
                    competitions           INTEGER NOT NULL,
                    gender                 TEXT,
                    games_vs_male          INTEGER NOT NULL,
                    games_vs_female        INTEGER NOT NULL,
                    last_competition_date  TEXT
                );

                -- Which competitions have already been folded into rankings2_state, so a later run
                -- only processes genuinely new ones instead of replaying everything.
                CREATE TABLE IF NOT EXISTS rankings2_processed_competitions (
                    competition_id INTEGER PRIMARY KEY
                );

                -- One row per player per tournament they played: what they had entering it, whatever
                -- correction the engine applied before the games were scored, their seed rating if
                -- this was their first tournament, and what they ended up with.
                CREATE TABLE IF NOT EXISTS rankings2_tournament_results (
                    player_id           INTEGER NOT NULL,
                    competition_id      INTEGER NOT NULL,
                    points_before       INTEGER NOT NULL,
                    correction          INTEGER,
                    is_first_tournament INTEGER NOT NULL,
                    initial_rating      INTEGER,
                    points_after        INTEGER NOT NULL,
                    PRIMARY KEY (player_id, competition_id)
                );

                -- One row per player per game: exactly what that one game moved their rating by.
                CREATE TABLE IF NOT EXISTS rankings2_game_results (
                    game_id       INTEGER NOT NULL,
                    player_id     INTEGER NOT NULL,
                    opponent_id   INTEGER NOT NULL,
                    points_before INTEGER NOT NULL,
                    points_after  INTEGER NOT NULL,
                    PRIMARY KEY (game_id, player_id)
                )";
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task ResetAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM rankings2;
                DELETE FROM rankings2_state;
                DELETE FROM rankings2_processed_competitions;
                DELETE FROM rankings2_tournament_results;
                DELETE FROM rankings2_game_results;";
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<HashSet<int>> LoadProcessedCompetitionIdsAsync(SqliteConnection con)
        {
            var result = new HashSet<int>();
            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT competition_id FROM rankings2_processed_competitions";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Add(r.GetInt32(0));
            return result;
        }

        private static async Task SaveProcessedCompetitionIdsAsync(SqliteConnection con, SqliteTransaction tr, HashSet<int> ids)
        {
            var cmd = con.CreateCommand();
            cmd.Transaction = tr;
            cmd.CommandText = "INSERT OR IGNORE INTO rankings2_processed_competitions (competition_id) VALUES ($id)";
            var param = cmd.CreateParameter();
            param.ParameterName = "$id";
            cmd.Parameters.Add(param);

            foreach (var id in ids)
            {
                param.Value = id;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task<Dictionary<int, AltPRating>> LoadStateAsync(SqliteConnection con)
        {
            var result = new Dictionary<int, AltPRating>();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT player_id, points, games_played, competitions, gender,
                       games_vs_male, games_vs_female, last_competition_date
                FROM rankings2_state";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result[r.GetInt32(0)] = new AltPRating
                {
                    Points = r.GetInt32(1),
                    GamesPlayed = r.GetInt32(2),
                    Competitions = r.GetInt32(3),
                    Gender = r.IsDBNull(4) ? null : r.GetString(4),
                    GamesVsMale = r.GetInt32(5),
                    GamesVsFemale = r.GetInt32(6),
                    LastCompetitionDate = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7)),
                };
            }

            return result;
        }

        private static async Task SaveStateAsync(SqliteConnection con, SqliteTransaction tr, Dictionary<int, AltPRating> rating)
        {
            foreach (var (playerId, p) in rating)
            {
                var cmd = con.CreateCommand();
                cmd.Transaction = tr;
                cmd.CommandText = @"
                    INSERT INTO rankings2_state
                        (player_id, points, games_played, competitions, gender, games_vs_male, games_vs_female, last_competition_date)
                    VALUES
                        ($p, $pts, $gp, $comp, $g, $gvm, $gvf, $lcd)
                    ON CONFLICT(player_id) DO UPDATE SET
                        points = excluded.points,
                        games_played = excluded.games_played,
                        competitions = excluded.competitions,
                        gender = excluded.gender,
                        games_vs_male = excluded.games_vs_male,
                        games_vs_female = excluded.games_vs_female,
                        last_competition_date = excluded.last_competition_date";
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$pts", p.Points);
                cmd.Parameters.AddWithValue("$gp", p.GamesPlayed);
                cmd.Parameters.AddWithValue("$comp", p.Competitions);
                cmd.Parameters.AddWithValue("$g", (object?)p.Gender ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$gvm", p.GamesVsMale);
                cmd.Parameters.AddWithValue("$gvf", p.GamesVsFemale);
                cmd.Parameters.AddWithValue("$lcd", (object?)p.LastCompetitionDate?.ToString("o") ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task SaveTournamentResultsAsync(SqliteConnection con, SqliteTransaction tr, List<AltTournamentDelta> deltas)
        {
            foreach (var d in deltas)
            {
                var cmd = con.CreateCommand();
                cmd.Transaction = tr;
                cmd.CommandText = @"
                    INSERT INTO rankings2_tournament_results
                        (player_id, competition_id, points_before, correction, is_first_tournament, initial_rating, points_after)
                    VALUES
                        ($p, $c, $before, $corr, $first, $init, $after)
                    ON CONFLICT(player_id, competition_id) DO UPDATE SET
                        points_before = excluded.points_before,
                        correction = excluded.correction,
                        is_first_tournament = excluded.is_first_tournament,
                        initial_rating = excluded.initial_rating,
                        points_after = excluded.points_after";
                cmd.Parameters.AddWithValue("$p", d.PlayerId);
                cmd.Parameters.AddWithValue("$c", d.CompetitionId);
                cmd.Parameters.AddWithValue("$before", d.PointsBefore);
                cmd.Parameters.AddWithValue("$corr", (object?)d.Correction ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$first", d.IsFirstTournament ? 1 : 0);
                cmd.Parameters.AddWithValue("$init", (object?)d.InitialRating ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$after", d.PointsAfter);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task SaveGameResultsAsync(SqliteConnection con, SqliteTransaction tr, List<AltGameDelta> deltas)
        {
            foreach (var d in deltas)
            {
                var cmd = con.CreateCommand();
                cmd.Transaction = tr;
                cmd.CommandText = @"
                    INSERT INTO rankings2_game_results
                        (game_id, player_id, opponent_id, points_before, points_after)
                    VALUES
                        ($g, $p, $o, $before, $after)
                    ON CONFLICT(game_id, player_id) DO UPDATE SET
                        opponent_id = excluded.opponent_id,
                        points_before = excluded.points_before,
                        points_after = excluded.points_after";
                cmd.Parameters.AddWithValue("$g", d.GameId);
                cmd.Parameters.AddWithValue("$p", d.PlayerId);
                cmd.Parameters.AddWithValue("$o", d.OpponentId);
                cmd.Parameters.AddWithValue("$before", d.PointsBefore);
                cmd.Parameters.AddWithValue("$after", d.PointsAfter);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── Loading ──────────────────────────────────────────────────────────

        private static async Task<List<(string MonthKey, List<AltCompetition> Comps)>> LoadCompetitionsByMonthAsync(SqliteConnection con)
        {
            var byMonth = new SortedDictionary<string, List<AltCompetition>>(StringComparer.Ordinal);

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id, start_date, coef FROM competitions ORDER BY start_date";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                if (r.IsDBNull(1) || !DateTime.TryParse(r.GetString(1), out var start)) continue;

                var comp = new AltCompetition
                {
                    Id = r.GetInt32(0),
                    StartDate = start,
                    Coef = r.IsDBNull(2) ? 1.0 : r.GetDouble(2),
                };

                string key = comp.StartDate.ToString("yyyy-MM");
                if (!byMonth.TryGetValue(key, out var list))
                    byMonth[key] = list = [];
                list.Add(comp);
            }

            return byMonth.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private static async Task<Dictionary<int, List<(int GameId, int P1, int P2, int S1, int S2)>>> LoadGamesByCompetitionAsync(SqliteConnection con)
        {
            var result = new Dictionary<int, List<(int, int, int, int, int)>>();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT id, competition_id, player1_id, player2_id, player1_sets, player2_sets
                FROM games
                WHERE player1_id IS NOT NULL AND player2_id IS NOT NULL
                  AND player1_sets IS NOT NULL AND player2_sets IS NOT NULL";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                int compId = r.GetInt32(1);
                if (!result.TryGetValue(compId, out var list))
                    result[compId] = list = [];
                list.Add((r.GetInt32(0), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
            }

            return result;
        }

        /// <summary>Id -> Gender for every PlayerDB row. Asked for explicitly: this engine reads a
        /// player's gender (and id/name/surname) from PlayerDB, unlike the ported tool's own Run()
        /// method, which never actually looked gender up and silently treated everyone as unknown.</summary>
        private static async Task<Dictionary<int, string?>> LoadGendersAsync(SqliteConnection con)
        {
            var result = new Dictionary<int, string?>();

            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Id, Gender FROM PlayerDB";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result[r.GetInt32(0)] = r.IsDBNull(1) ? null : r.GetString(1);

            return result;
        }

        private static async Task SaveMonthAsync(SqliteConnection con, SqliteTransaction tr, string month, Dictionary<int, AltPRating> rating)
        {
            DateTime currentMonth = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", null);
            DateTime threshold = currentMonth.AddYears(-2);

            foreach (var (playerId, p) in rating)
            {
                bool isActive = p.LastCompetitionDate != null && p.LastCompetitionDate >= threshold;

                var cmd = con.CreateCommand();
                cmd.Transaction = tr;
                cmd.CommandText = @"
                    INSERT INTO rankings2 (player_id, month, points, is_active)
                    VALUES ($p, $m, $pts, $active)
                    ON CONFLICT(player_id, month) DO UPDATE SET
                        points = excluded.points,
                        is_active = excluded.is_active";
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$m", month);
                cmd.Parameters.AddWithValue("$pts", p.Points);
                cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // ── Calculation (ported from the desktop tool's RankingEngine2) ────────

        private static void EnsurePlayer(Dictionary<int, AltPRating> rating, int id, Dictionary<int, string?> genderByPlayer)
        {
            if (!rating.ContainsKey(id))
            {
                rating[id] = new AltPRating
                {
                    Points = 1000,
                    Competitions = 0,
                    GamesPlayed = 0,
                    Gender = genderByPlayer.GetValueOrDefault(id),
                    GamesVsMale = 0,
                    GamesVsFemale = 0,
                };
            }
        }

        private static List<AltGameDelta> CalculateCompetition(AltCompetition comp, List<(int GameId, int P1, int P2, int S1, int S2)> games, Dictionary<int, AltPRating> rating, Dictionary<int, string?> genderByPlayer)
        {
            var deltas = new List<AltGameDelta>();

            foreach (var (gameId, p1, p2, s1, s2) in games)
            {
                EnsurePlayer(rating, p1, genderByPlayer);
                EnsurePlayer(rating, p2, genderByPlayer);

                int r1 = rating[p1].Points;
                int r2 = rating[p2].Points;

                double e1 = Elo.Expected(r1, r2);
                double e2 = Elo.Expected(r2, r1);

                double score1 = s1 > s2 ? 1 : 0;
                double score2 = s2 > s1 ? 1 : 0;

                double coef1 = comp.Coef;
                double coef2 = comp.Coef;

                var g1 = rating[p1].Gender;
                var g2 = rating[p2].Gender;

                if (g1 == "female" && g2 == "female")
                {
                    double trust1 = rating[p1].GamesVsMale / (double)(rating[p1].GamesVsMale + rating[p1].GamesVsFemale + 1);
                    double trust2 = rating[p2].GamesVsMale / (double)(rating[p2].GamesVsMale + rating[p2].GamesVsFemale + 1);
                    coef1 *= 0.3 + trust1;
                    coef2 *= 0.3 + trust2;
                }

                int c1 = rating[p1].Competitions;
                int c2 = rating[p2].Competitions;

                if (g1 == null) coef1 *= 2.5;
                if (g2 == null) coef2 *= 2.5;

                if (c1 <= 10) coef1 *= 1.5;
                if (c2 <= 10) coef2 *= 1.5;

                if (c1 > 10 && c1 < 30) coef1 *= 1.2;
                if (c2 > 10 && c2 < 30) coef2 *= 1.2;

                coef1 = Math.Min(coef1, 16);
                coef2 = Math.Min(coef2, 16);

                int new1 = Math.Max(200, Elo.NewRating(r1, score1, e1, coef1));
                int new2 = Math.Max(200, Elo.NewRating(r2, score2, e2, coef2));

                deltas.Add(new AltGameDelta { GameId = gameId, PlayerId = p1, OpponentId = p2, PointsBefore = r1, PointsAfter = new1 });
                deltas.Add(new AltGameDelta { GameId = gameId, PlayerId = p2, OpponentId = p1, PointsBefore = r2, PointsAfter = new2 });

                rating[p1].Points = new1;
                rating[p2].Points = new2;
            }

            return deltas;
        }

        private static Dictionary<int, AltPlayerResult> GetCompetitionResults(AltCompetition comp, List<(int GameId, int P1, int P2, int S1, int S2)> games, Dictionary<int, AltPRating> rating, Dictionary<int, string?> genderByPlayer)
        {
            var result = new Dictionary<int, AltPlayerResult>();

            foreach (var (_, player1, player2, s1, s2) in games)
            {
                EnsurePlayer(rating, player1, genderByPlayer);
                EnsurePlayer(rating, player2, genderByPlayer);

                if (!result.TryGetValue(player1, out var res1))
                    result[player1] = res1 = new AltPlayerResult { Coef = comp.Coef };
                if (!result.TryGetValue(player2, out var res2))
                    result[player2] = res2 = new AltPlayerResult { Coef = comp.Coef };

                res1.PlayerId = player1;
                res2.PlayerId = player2;

                int r1 = rating[player1].Points;
                int r2 = rating[player2].Points;
                res1.StartRating = r1;
                res2.StartRating = r2;

                int c1 = rating[player1].Competitions;
                int c2 = rating[player2].Competitions;

                string? g1 = rating[player1].Gender;
                string? g2 = rating[player2].Gender;
                res1.Gender = g1;
                res2.Gender = g2;

                if (g2 == "male") rating[player1].GamesVsMale++;
                else if (g2 == "female") rating[player1].GamesVsFemale++;

                if (g1 == "male") rating[player2].GamesVsMale++;
                else if (g1 == "female") rating[player2].GamesVsFemale++;

                rating[player1].GamesPlayed++;
                rating[player2].GamesPlayed++;

                rating[player1].LastCompetitionDate = comp.StartDate;
                rating[player2].LastCompetitionDate = comp.StartDate;

                if (s1 > s2)
                {
                    res1.WinsVs.Add((player2, c2, g2));
                    res2.LossVs.Add((player1, c1, g1));
                }
                else if (s2 > s1)
                {
                    res2.WinsVs.Add((player1, c1, g1));
                    res1.LossVs.Add((player2, c2, g2));
                }
            }

            return result;
        }

        private static int CalculateInitialFromResults(AltPlayerResult p, Dictionary<int, AltPRating> rating)
        {
            int w = p.WinsVs.Count;
            int l = p.LossVs.Count;
            bool bigComp;

            if (w == 0 && l == 0)
                return p.AfterInitial = p.StartRating;

            if (w == 0)
            {
                int minOpp = p.LossVs.Min(x => rating[x.OpponentId].Points);
                if (minOpp > 1000) return p.AfterInitial = 1000;
                if (minOpp < 750) return p.AfterInitial = 750;
                return p.AfterInitial = minOpp - 20;
            }

            if (l == 0)
            {
                int maxOpp = p.WinsVs.Max(x => rating[x.OpponentId].Points);
                if (maxOpp < p.StartRating) return p.AfterInitial = p.StartRating;
                if (maxOpp < 1000) return p.AfterInitial = 1000;

                bigComp = p.Coef >= 2;
                if (bigComp)
                    return p.AfterInitial = maxOpp + (p.Gender == null ? 200 : 50);
                return p.AfterInitial = maxOpp + (p.Gender == null ? 50 : 20);
            }

            int n = Math.Min(w, l);
            var winsAll = p.WinsVs.OrderByDescending(x => rating[x.OpponentId].Points).ToList();
            var lossesAll = p.LossVs.OrderBy(x => rating[x.OpponentId].Points).ToList();

            if (n == 1)
            {
                if (winsAll.Count >= 2 && lossesAll.Count >= 1)
                {
                    int topWin = rating[winsAll[0].OpponentId].Points;
                    int worstLoss = rating[lossesAll[0].OpponentId].Points;
                    double avgWins = winsAll.Take(3).Select(x => rating[x.OpponentId].Points).Average();
                    double avgLosses = lossesAll.Take(3).Select(x => rating[x.OpponentId].Points).Average();

                    if (worstLoss - avgWins > 400)
                        return p.AfterInitial = Math.Max(750, (int)avgWins + 100);
                    if (topWin - avgLosses > 400)
                        return p.AfterInitial = Math.Max(750, (int)avgLosses - 100);
                }

                if (winsAll.Count >= 3)
                {
                    int topWin = rating[winsAll[0].OpponentId].Points;
                    double avgOthers = winsAll.Skip(1).Take(3).Select(x => rating[x.OpponentId].Points).Average();
                    if (topWin - avgOthers > 300)
                        return p.AfterInitial = Math.Max(750, (int)avgOthers + 200);
                }

                if (lossesAll.Count >= 3)
                {
                    int worstLoss = rating[lossesAll[0].OpponentId].Points;
                    double avgOthers = lossesAll.Skip(1).Take(3).Select(x => rating[x.OpponentId].Points).Average();
                    if (avgOthers - worstLoss > 300)
                        return p.AfterInitial = Math.Max(750, (int)avgOthers - 200);
                }
            }

            var wins = winsAll.Take(n).ToList();
            var losses = lossesAll.Take(n).ToList();
            if (n >= 2)
            {
                wins.RemoveAt(0);              // strongest win
                losses.RemoveAt(losses.Count - 1);   // weakest loss
            }

            var all = wins.Concat(losses).ToList();
            if (all.Count == 0)
                all = winsAll.Take(2).Concat(lossesAll.Take(2)).ToList();

            double sum = 0, weightSum = 0;
            foreach (var a in all)
            {
                double weight = a.Comps + 1;
                sum += rating[a.OpponentId].Points * weight;
                weightSum += weight;
            }
            int avg = (int)(sum / weightSum);

            int strongestWin = winsAll.Count > 0 ? rating[winsAll[0].OpponentId].Points : avg;
            int weakestLoss = lossesAll.Count > 0 ? rating[lossesAll[0].OpponentId].Points : avg;

            if (strongestWin - avg > 400) avg = (int)(avg * 0.7 + strongestWin * 0.3);
            if (avg - weakestLoss > 400) avg = (int)(avg * 0.7 + weakestLoss * 0.3);

            bigComp = p.Coef >= 2;
            int bonus = bigComp
                ? (p.Gender == null ? 50 : 10)
                : (p.Gender == null ? 20 : 10);

            return p.AfterInitial = Math.Max(750, bonus + avg);
        }

        private static int? CheckCorrectionUp(AltPlayerResult p, Dictionary<int, AltPRating> rating)
        {
            int baseRating = p.AfterInitial > p.StartRating ? p.AfterInitial : p.StartRating;

            var wins = p.WinsVs.Where(x => x.Comps >= 1).ToList();
            var losses = p.LossVs.ToList();
            if (wins.Count == 0) return null;

            var bigWins = wins.Where(x => rating[x.OpponentId].Points - baseRating >= 400).ToList();
            var superWins = wins.Where(x => rating[x.OpponentId].Points - baseRating >= 700).ToList();
            bool isSuperWin = superWins.Count > 1;
            var countedLosses = losses.Where(x => Math.Abs(rating[x.OpponentId].Points - baseRating) <= 200);

            if (bigWins.Count >= 2 || isSuperWin)
            {
                var all = isSuperWin ? wins.Concat(p.LossVs).ToList() : bigWins.Concat(countedLosses).ToList();
                if (all.Count < 2) return null;

                int avgRating = (int)all.Average(x => rating[x.OpponentId].Points);
                if (avgRating < baseRating) return null;
                if (avgRating - baseRating > 400) return baseRating + 300;
                return avgRating;
            }

            return null;
        }

        private static int? CheckCorrectionDown(AltPlayerResult p, Dictionary<int, AltPRating> rating)
        {
            int baseRating = p.AfterInitial > p.StartRating ? p.AfterInitial : p.StartRating;

            var losses = p.LossVs.Where(x => x.Comps >= 1).ToList();
            var wins = p.WinsVs.ToList();
            if (losses.Count == 0) return null;

            var bigLosses = losses.Where(x => baseRating - rating[x.OpponentId].Points >= 500).ToList();
            var countedWins = wins.Where(x => Math.Abs(rating[x.OpponentId].Points - baseRating) <= 200);

            if (bigLosses.Count >= 4)
            {
                var all = bigLosses.Concat(countedWins).ToList();
                if (all.Count < 4) return null;

                int avg = (int)all.Average(x => rating[x.OpponentId].Points);
                return Math.Max(400, avg);
            }

            return null;
        }

        private static (List<AltTournamentDelta> TournamentDeltas, List<AltGameDelta> GameDeltas) CalculateCompetitionAdvanced(
            AltCompetition comp, List<(int GameId, int P1, int P2, int S1, int S2)> games, Dictionary<int, AltPRating> rating, Dictionary<int, string?> genderByPlayer)
        {
            var results = GetCompetitionResults(comp, games, rating, genderByPlayer);
            var tempRatings = rating.ToDictionary(x => x.Key, x => x.Value.Points);

            // Captured once, before the correction loop touches anything, for the per-tournament
            // record: was this the player's first tournament, and what did the engine seed them at.
            var isFirstTournament = results.Keys.ToDictionary(id => id, id => !rating.TryGetValue(id, out var rp0) || rp0.Competitions == 0);
            var initialRatingByPlayer = new Dictionary<int, int>();

            const int MaxIter = 3;
            for (int iter = 0; iter < MaxIter; iter++)
            {
                bool changed = false;

                foreach (var (id, pts) in tempRatings)
                    if (rating.TryGetValue(id, out var pr)) pr.Points = pts;

                foreach (var p in results.Values)
                {
                    int oldRating = tempRatings[p.PlayerId];
                    int newRating = oldRating;

                    bool needsInitial = !rating.TryGetValue(p.PlayerId, out var rp) || rp.Competitions == 0;
                    bool foreignUnstable = rp != null && rp.Gender == null && rp.Competitions < 2;

                    if (needsInitial || foreignUnstable)
                    {
                        int candidate = CalculateInitialFromResults(p, rating);
                        if (isFirstTournament[p.PlayerId]) initialRatingByPlayer[p.PlayerId] = candidate;

                        if (rp == null || Math.Abs(candidate - oldRating) > 100 || rp.Gender == null)
                        {
                            if (foreignUnstable && oldRating - candidate > 50)
                                newRating = oldRating;
                            else
                                newRating = candidate;
                        }
                    }

                    var up = CheckCorrectionUp(p, rating);
                    if (up.HasValue) newRating = up.Value;

                    var down = CheckCorrectionDown(p, rating);
                    if (down.HasValue) newRating = down.Value;

                    if (Math.Abs(newRating - oldRating) > 5)
                    {
                        tempRatings[p.PlayerId] = newRating;
                        changed = true;
                    }
                }

                if (!changed) break;
            }

            // The rating each player enters their games with, after any initial-seeding/correction
            // above but before this competition's own games are scored - this is "the correction".
            var pointsEnteringGames = new Dictionary<int, int>(tempRatings);

            foreach (var (id, pts) in tempRatings)
            {
                if (!rating.TryGetValue(id, out var pr))
                {
                    rating[id] = new AltPRating { Points = pts, Gender = genderByPlayer.GetValueOrDefault(id) };
                }
                else
                {
                    pr.Points = pts;
                }
            }

            var gameDeltas = CalculateCompetition(comp, games, rating, genderByPlayer);

            var tournamentDeltas = new List<AltTournamentDelta>();
            foreach (var p in results.Values)
            {
                int pointsBefore = p.StartRating;
                int enteringGames = pointsEnteringGames[p.PlayerId];
                bool firstTournament = isFirstTournament[p.PlayerId];

                tournamentDeltas.Add(new AltTournamentDelta
                {
                    PlayerId          = p.PlayerId,
                    CompetitionId     = comp.Id,
                    PointsBefore      = pointsBefore,
                    Correction        = enteringGames != pointsBefore ? enteringGames : null,
                    IsFirstTournament = firstTournament,
                    InitialRating     = firstTournament && initialRatingByPlayer.TryGetValue(p.PlayerId, out var ir) ? ir : null,
                    PointsAfter       = rating[p.PlayerId].Points,
                });

                rating[p.PlayerId].Competitions++;
            }

            return (tournamentDeltas, gameDeltas);
        }
    }
}
