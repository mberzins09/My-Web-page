using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Imports competitions and games from turniri.lgtf.lv API into lgtf.sqlite.
    ///
    /// Two import modes:
    ///   ImportSinglesAndTeamsAsync — singles + teams(is_season=false), uses import_log.last_singles_date
    ///   ImportSeasonTeamsAsync    — teams(is_season=true) split per playing_date, uses import_log.last_teams_season_date
    /// </summary>
    public class LgtfImportService
    {
        private const string TournamentApiBase = "https://turniri.lgtf.lv/api/v1";
        private const string TournamentApiKey = "org_trJaxebjAq9bQjdkPb1PJONCO1Im8befEFv7w8Jr";

        private readonly string _cs;
        private readonly HttpClient _http;

        private int _reqCount = 0;
        private DateTime _windowStart = DateTime.Now;

        private readonly Dictionary<int, TtRankedPlayer> _playerCache = new();
        private readonly LgtfRankingService _rankingService;

        public bool IsRunning { get; private set; }

        public LgtfImportService(IConfiguration config, IHttpClientFactory httpFactory, LgtfRankingService rankingService)
        {
            _cs = config.GetConnectionString("LgtfConnection") ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
            _http = httpFactory.CreateClient("lgtf");
            _http.DefaultRequestHeaders.Add("x-api-key", TournamentApiKey);
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
            _http.Timeout = TimeSpan.FromSeconds(90);
            _rankingService = rankingService;
        }

        public async Task<int> GetOrCreatePlayer(string name, string surname)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            await EnsureSchemaAsync(con);
            await EnsureImportLogSchemaAsync(con);

            return await GetOrCreatePlayerAsync(con, name, surname);
        }

        // ====================================================================
        //  Entry point 1 — Singles + Teams (non-season)
        // ====================================================================
        public async Task ImportSinglesAndTeamsAsync(Action<string> progress)
        {
            if (IsRunning) 
            { 
                progress("⚠️ Import already running.");

                return; 
            }

            IsRunning = true;
            _reqCount = 0;
            _windowStart = DateTime.Now;
            _playerCache.Clear();

            try
            {
                await using var con = new SqliteConnection(_cs);
                await con.OpenAsync();

                await EnsureSchemaAsync(con);
                await EnsureImportLogSchemaAsync(con);

                var log = await GetImportLogAsync(con);
                progress($"Last singles import date: {log.LastSinglesDate?.ToString("yyyy-MM-dd") ?? "(none — will fetch all)"}");

                // 14-day safety window
                DateTime fromDate = (log.LastSinglesDate ?? DateTime.Today.AddYears(-10)).AddDays(-14);
                string fromDateStr = fromDate.ToString("yyyy-MM-dd");
                progress($"Fetching competition-events from {fromDateStr}...");

                // Load already-imported external ids
                var knownIds = await LoadKnownExternalIdsAsync(con);
                progress($"  {knownIds.Count} competitions already in DB.");

                // Fetch singles
                await RateLimitAsync();
                var singlesEvents = await GetCompetitionEventsAsync("singles", fromDateStr);
                progress($"  {singlesEvents.Count} singles events from API.");

                // Fetch teams (non-season only)
                await RateLimitAsync();
                var allTeams = await GetCompetitionEventsAsync("teams", fromDateStr);
                var teamsEvents = allTeams.Where(e => !e.is_season_ranking_instance).ToList();
                progress($"  {teamsEvents.Count} teams (non-season) events from API.");

                // Skip already-imported
                var toImport = singlesEvents.Concat(teamsEvents).Where(e => !knownIds.Contains(e.id)).ToList();
                progress($"  {toImport.Count} new events to import.\n");

                // When already up to date, update the date to Today
                if (toImport.Count == 0)
                {
                    progress("✅ Already up to date.");
                    await UpdateSinglesImportDateAsync(con, DateTime.Today);
                    progress($"  Date updated to: {DateTime.Today:yyyy-MM-dd}");

                    return;
                }

                DateTime? maxDate = log.LastSinglesDate;
                int done = 0;

                foreach (var ev in toImport)
                {
                    progress($"[{++done}/{toImport.Count}] Event {ev.id} ({ev.type}) — {ev.name}");
                    try
                    {
                        await RateLimitAsync();

                        var result = await GetEventResultsAsync(ev.id, null);
                        if (result?.competition_event == null)
                        {
                            progress("  Skipped (no data).");
                            continue;
                        }

                        var ce = result.competition_event;
                        string name = BuildTournamentName(ce.name, ce.competition?.name);
                        string places = ce.competition?.places ?? "";
                        double coef = ParseCoef(ce.ranking_coef);

                        if (result.nets == null || result.nets.Count == 0)
                        {
                            progress("  No games yet.");
                            continue;
                        }

                        if (await CompetitionExistsAsync(con, ev.id, ce.start_date))
                        {
                            progress("  Already in DB — skipped.");
                            continue;
                        }

                        int compId = await InsertCompetitionAsync(con, name, ce.start_date, places, coef, ev.id, ev.type);
                        progress($"  → {name}  ({ce.start_date})");

                        var games = CollectGames(result);
                        int inserted = await InsertGamesAsync(con, compId, games, DateTime.Parse(ce.start_date));
                        progress($"  {inserted} games inserted.");

                        if (inserted > 0 && DateTime.TryParse(ce.start_date, out var cd))
                        {
                            await _rankingService.RecalculateTournamentAsync(compId, cd, progress);

                            if (maxDate == null || cd > maxDate)
                            { 
                                maxDate = cd; 
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        progress($"⚠️ Error for event {ev.id}: {ex}");
                    }
                }

                if (maxDate.HasValue)
                {
                    await UpdateSinglesImportDateAsync(con, maxDate.Value);
                }

                progress($"\n✅ Import complete. Last date saved: {maxDate?.ToString("yyyy-MM-dd") ?? "(none)"}");
            }
            finally { IsRunning = false; }
        }

        // ====================================================================
        //  Entry point 2 — Season Teams (is_season_ranking_instance = true)
        // ====================================================================

        public async Task ImportSeasonTeamsAsync(DateTime fromDate, Action<string> progress)
        {
            if (IsRunning) 
            { 
                progress("⚠️ Import already running."); 
                
                return; 
            }

            IsRunning = true;
            _reqCount = 0;
            _windowStart = DateTime.Now;
            _playerCache.Clear();

            try
            {
                await using var con = new SqliteConnection(_cs);
                await con.OpenAsync();

                await EnsureSchemaAsync(con);
                await EnsureImportLogSchemaAsync(con);

                var log = await GetImportLogAsync(con);
                progress($"Last season-teams import date: {log.LastTeamsDate?.ToString("yyyy-MM-dd") ?? "(none)"}");

                string fromDateStr = fromDate.ToString("yyyy-MM-dd");
                progress($"Fetching season teams events from {fromDateStr}...");

                await RateLimitAsync();
                var allTeams = await GetCompetitionEventsAsync("teams", fromDateStr);
                var seasonEvents = allTeams.Where(e => e.is_season_ranking_instance).ToList();
                progress($"  {seasonEvents.Count} season events found.");

                // ── One-time cleanup on first run ───────────────────────────
                if (!log.TeamsSeasonCleanupDone)
                {
                    progress("⚠️ First run: removing incorrectly imported season tournaments...");
                    var seasonIds = allTeams.Where(e => e.is_season_ranking_instance).Select(e => e.id).ToHashSet();
                    // Also grab ALL teams season events (no from_date filter) for cleanup
                    await RateLimitAsync();
                    var allSeasonTeams = await GetCompetitionEventsAsync("teams", null);
                    foreach (var e in allSeasonTeams.Where(e => e.is_season_ranking_instance))
                        seasonIds.Add(e.id);

                    int cleaned = await CleanSeasonTournamentsAsync(con, seasonIds, progress);
                    await SetTeamsSeasonCleanupDoneAsync(con);
                    progress($"  Cleanup done — {cleaned} old tournament(s) removed.");
                }

                // ── Build eventId → list of playing dates ──────────────────
                // Each season event's season_rankings has per-player entries with playing_date.
                // We collect distinct dates per event, skipping those already imported.
                DateTime? dateCutoff = log.LastTeamsDate;
                var queued = new HashSet<string>();
                var evLookup = new Dictionary<int, TtCompetitionEventListItem>();
                var eventDates = new Dictionary<int, List<DateTime>>();

                foreach (var ev in seasonEvents)
                {
                    if (!evLookup.ContainsKey(ev.id))
                    {
                        evLookup[ev.id] = ev;
                    }

                    _ = DateTime.TryParse(ev.end_date, out DateTime endDate);
                    _ = DateTime.TryParse(ev.start_date, out DateTime startDate);

                    void TryAddDate(DateTime dt)
                    {
                        if (dt == DateTime.MinValue)
                        { 
                            return; 
                        }

                        if (dateCutoff.HasValue && dt.Date <= dateCutoff.Value.Date)
                        { 
                            return; 
                        }

                        string key = $"{ev.id}:{dt:yyyy-MM-dd}";
                        if (!queued.Add(key))
                        {
                            return; // already queued this (eventId, date)
                        }

                        if (!eventDates.ContainsKey(ev.id))
                        {
                            eventDates[ev.id] = [];
                        }

                        eventDates[ev.id].Add(dt.Date);
                    }

                    TryAddDate(startDate);
                    TryAddDate(endDate);
                }

                // Sort each event's dates ascending
                foreach (var kv in eventDates)
                { 
                    kv.Value.Sort(); 
                }

                // Remove events that ended up with no dates to import
                foreach (var key in eventDates.Keys.Where(k => eventDates[k].Count == 0).ToList())
                {
                    eventDates.Remove(key);
                }

                int totalCombos = eventDates.Sum(kv => kv.Value.Count);
                progress($"  {totalCombos} (event × date) rounds to import after cutoff.");

                if (totalCombos == 0)
                {
                    progress("✅ No new season-teams rounds to import.");
                    await UpdateTeamsImportDateAsync(con, DateTime.Today);
                    progress($"  Date updated to: {DateTime.Today:yyyy-MM-dd}");
                    return;
                }

                DateTime? maxDate = log.LastTeamsDate;
                int done = 0;

                foreach (var (eventId, dates) in eventDates)
                {
                    if (!evLookup.TryGetValue(eventId, out var evItem))
                    { 
                        continue; 
                    }

                    foreach (var playDate in dates)
                    {
                        string playDateStr = playDate.ToString("yyyy-MM-dd");
                        progress($"[{++done}/{totalCombos}] Event {eventId} — {playDateStr}  ({evItem.name})");

                        try
                        {
                            await RateLimitAsync();
                            var result = await GetEventResultsAsync(eventId, playDateStr);
                            if (result?.competition_event == null)
                            {
                                progress("  Skipped (no data).");
                                continue;
                            }

                            var ce = result.competition_event;
                            string nm = BuildSeasonTournamentName(ce.competition?.name ?? evItem.competition?.name ?? "", ce.name, playDateStr);
                            string places = ce.competition?.places ?? "";
                            double coef = ParseCoef(ce.ranking_coef);

                            if (result.nets == null || result.nets.Count == 0)
                            {
                                progress("  No games for this date.");
                                continue;
                            }

                            if (await CompetitionExistsAsync(con, eventId, playDateStr))
                            {
                                progress("  Already in DB — skipped.");
                                continue;
                            }

                            int compId = await InsertCompetitionAsync(con, nm, playDateStr, places, coef, eventId, "teams_season");
                            progress($"  → {nm}");


                            var games = CollectGames(result);
                            int inserted = await InsertGamesAsync(con, compId, games, DateTime.Parse(ce.start_date));
                            progress($"  {inserted} games inserted.");

                            if (inserted > 0)
                            {
                                    await _rankingService.RecalculateTournamentAsync(compId, playDate, progress);

                                if (maxDate == null || playDate > maxDate.Value)
                                {
                                    maxDate = playDate;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            progress($"  ⚠️ Error for event {eventId} / {playDateStr}: {ex.Message}");
                        }
                    }
                }

                if (maxDate.HasValue)
                    await UpdateTeamsImportDateAsync(con, maxDate.Value);

                progress($"\n✅ Season teams import complete. Last date: {maxDate?.ToString("yyyy-MM-dd") ?? "(none)"}");
            }
            finally { IsRunning = false; }
        }

        // ====================================================================
        //  Schema
        // ====================================================================

        private static async Task EnsureSchemaAsync(SqliteConnection con)
        {
            string[] alters =
            [
                "ALTER TABLE games ADD COLUMN player1_keyName TEXT DEFAULT ''",
                "ALTER TABLE games ADD COLUMN player2_keyName TEXT DEFAULT ''",
                "ALTER TABLE games ADD COLUMN player1_points INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player2_points INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player1_PointsWithBonus INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player2_PointsWithBonus INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player1_age INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player2_age INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player1_place INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN player2_place INTEGER DEFAULT 0",
                "ALTER TABLE games ADD COLUMN ranking_data_filled INTEGER DEFAULT 0",
                "ALTER TABLE competitions ADD COLUMN external_event_id INTEGER DEFAULT 0",
                "ALTER TABLE competitions ADD COLUMN event_type TEXT DEFAULT 'singles'",
            ];
            foreach (var sql in alters)
            {
                try 
                { 
                    var c = con.CreateCommand(); c.CommandText = sql; 
                    await c.ExecuteNonQueryAsync(); 
                }
                catch 
                { 
                    /* column already exists */
                }
            }
        }

        private static async Task EnsureImportLogSchemaAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS import_log (
                    id                        INTEGER PRIMARY KEY,
                    import_date               TEXT,
                    last_singles_date         TEXT,
                    last_teams_season_date    TEXT,
                    teams_season_cleanup_done INTEGER DEFAULT 0
                )";

            await cmd.ExecuteNonQueryAsync();
        }

        // ====================================================================
        //  import_log helpers
        // ====================================================================

        private record ImportLog(DateTime? LastSinglesDate, DateTime? LastTeamsDate, bool TeamsSeasonCleanupDone);

        private static async Task<ImportLog> GetImportLogAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT last_singles_date, last_teams_season_date, teams_season_cleanup_done FROM import_log WHERE id = 1";
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                DateTime? s = r.IsDBNull(0) ? null : DateTime.TryParse(r.GetString(0), out var d1) ? d1 : null;
                DateTime? t = r.IsDBNull(1) ? null : DateTime.TryParse(r.GetString(1), out var d2) ? d2 : null;
                bool done = !r.IsDBNull(2) && r.GetInt32(2) == 1;

                return new ImportLog(s, t, done);
            }

            await r.DisposeAsync();

            // No row yet — insert the default so the UI can read it back immediately
            var defaultDate = new DateTime(2025, 10, 1);
            var ins = con.CreateCommand();
            ins.CommandText = @"
                INSERT OR IGNORE INTO import_log (id, import_date, last_singles_date, last_teams_season_date, teams_season_cleanup_done)
                VALUES (1, $now, $d, $d, 0)";
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            ins.Parameters.AddWithValue("$d", defaultDate.ToString("yyyy-MM-dd"));

            await ins.ExecuteNonQueryAsync();

            return new ImportLog(defaultDate, defaultDate, false);
        }

        private static async Task UpdateSinglesImportDateAsync(SqliteConnection con, DateTime date)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO import_log (id, import_date, last_singles_date)
                VALUES (1, $now, $d)
                ON CONFLICT(id) DO UPDATE SET
                    import_date       = excluded.import_date,
                    last_singles_date = excluded.last_singles_date";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd"));

            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task UpdateTeamsImportDateAsync(SqliteConnection con, DateTime date)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO import_log (id, import_date, last_teams_season_date)
                VALUES (1, $now, $d)
                ON CONFLICT(id) DO UPDATE SET
                    import_date            = excluded.import_date,
                    last_teams_season_date = excluded.last_teams_season_date";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd"));

            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SetTeamsSeasonCleanupDoneAsync(SqliteConnection con)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO import_log (id, import_date, teams_season_cleanup_done)
                VALUES (1, $now, 1)
                ON CONFLICT(id) DO UPDATE SET
                    import_date               = excluded.import_date,
                    teams_season_cleanup_done = 1";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync();
        }

        // ====================================================================
        //  DB helpers
        // ====================================================================

        private static async Task<HashSet<int>> LoadKnownExternalIdsAsync(SqliteConnection con)
        {
            var result = new HashSet<int>();
            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT external_event_id FROM competitions WHERE external_event_id > 0";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(r.GetInt32(0));
            }

            return result;
        }

        private static async Task<bool> CompetitionExistsAsync(SqliteConnection con, int externalId, string startDate)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM competitions WHERE external_event_id = $eid AND start_date = $d";
            cmd.Parameters.AddWithValue("$eid", externalId);
            cmd.Parameters.AddWithValue("$d", startDate);
            var result = await cmd.ExecuteScalarAsync();

            return Convert.ToInt32(result) > 0;
        }

        private static async Task<int> CleanSeasonTournamentsAsync(SqliteConnection con, HashSet<int> seasonEventIds, Action<string> progress)
        {
            if (seasonEventIds.Count == 0) return 0;

            var compIds = new List<int>();
            var sel = con.CreateCommand();
            sel.CommandText = "SELECT id, external_event_id FROM competitions WHERE external_event_id > 0";
            await using (var r = await sel.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    if (seasonEventIds.Contains(r.GetInt32(1)))
                        compIds.Add(r.GetInt32(0));

            if (compIds.Count == 0) return 0;

            await using var tr = await con.BeginTransactionAsync();
            foreach (var cid in compIds)
            {
                var dg = con.CreateCommand(); dg.Transaction = (SqliteTransaction)tr;
                dg.CommandText = "DELETE FROM games WHERE competition_id = $cid";
                dg.Parameters.AddWithValue("$cid", cid);
                int del = await dg.ExecuteNonQueryAsync();

                var dc = con.CreateCommand(); dc.Transaction = (SqliteTransaction)tr;
                dc.CommandText = "DELETE FROM competitions WHERE id = $cid";
                dc.Parameters.AddWithValue("$cid", cid);

                await dc.ExecuteNonQueryAsync();

                progress($"    Removed competition id={cid} ({del} games).");
            }

            await tr.CommitAsync();

            return compIds.Count;
        }

        private static async Task<int> InsertCompetitionAsync(SqliteConnection con, string name, string startDate, string places, double coef, int externalId, string eventType)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO competitions (name, start_date, places, coef, external_event_id, event_type)
                VALUES ($n, $d, $p, $c, $eid, $et);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", startDate);
            cmd.Parameters.AddWithValue("$p", places);
            cmd.Parameters.AddWithValue("$c", coef);
            cmd.Parameters.AddWithValue("$eid", externalId);
            cmd.Parameters.AddWithValue("$et", eventType);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<int> InsertGamesAsync(SqliteConnection con, int compId, List<TtGroupGame> games, DateTime tournamentDate)
        {
            int count = 0;
            foreach (var g in games)
            {
                int p1 = await GetOrCreatePlayerAsync(con, g.player1!.name, g.player1.surname);
                int p2 = await GetOrCreatePlayerAsync(con, g.player2!.name, g.player2.surname);
                int s1 = int.Parse(g.player1_score!);
                int s2 = int.Parse(g.player2_score!);

                var rp1 = await GetRankedPlayerCachedAsync(con, p1);
                var rp2 = await GetRankedPlayerCachedAsync(con, p2);

                await InsertGameAsync(con, compId, p1, p2, s1, s2, rp1, rp2, tournamentDate);
                count++;
            }

            return count;
        }

        private async Task<TtRankedPlayer> GetRankedPlayerCachedAsync(SqliteConnection con, int playerId)
        {
            // already loaded?
            if (_playerCache.TryGetValue(playerId, out var cached))
            {
                return cached;
            }

            // not cached -> DB lookup
            var player = await GetRankedPlayerAsync(con, playerId);

            _playerCache[playerId] = player;

            return player;
        }

        private static async Task<TtRankedPlayer> GetRankedPlayerAsync(SqliteConnection con, int playerId)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
        SELECT
            KeyName,
            Place,
            Points,
            PointsWithBonus,
            BirthDate,
            Name,
            Surname
        FROM PlayerDB
        WHERE Id = $id
        LIMIT 1";

            cmd.Parameters.AddWithValue("$id", playerId);

            await using var r = await cmd.ExecuteReaderAsync();

            if (!await r.ReadAsync())
                return new TtRankedPlayer();

            return new TtRankedPlayer
            {
                KeyName = r["KeyName"] == DBNull.Value ? "" : r["KeyName"].ToString()!,
                Place = r["Place"] == DBNull.Value ? 0 : Convert.ToInt32(r["Place"]),
                Points = r["Points"] == DBNull.Value ? 0 : Convert.ToInt32(r["Points"]),
                PointsWithBonus = r["PointsWithBonus"] == DBNull.Value ? 0 : Convert.ToInt32(r["PointsWithBonus"]),
                BirthDate = r["BirthDate"] == DBNull.Value ? "" : r["BirthDate"].ToString()!,
                Name = r["Name"] == DBNull.Value ? "" : r["Name"].ToString()!,
                Surname = r["Surname"] == DBNull.Value ? "" : r["Surname"].ToString()!
            };
        }

        private static async Task<int> GetOrCreatePlayerAsync(
    SqliteConnection con, string name, string surname)
        {
            string key = NormalizeKey(name + surname);

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
        INSERT OR IGNORE INTO PlayerDB
            (Name, Surname, KeyName,
             Points, PointsWithBonus, PointsChanged,
             IsActive, Place, OverallPlace)
        VALUES
            ($n, $s, $k,
             0, 0, 0,
             1, 0, 0);

        SELECT Id
        FROM PlayerDB
        WHERE KeyName = $k;";

            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$s", surname);
            cmd.Parameters.AddWithValue("$k", key);

            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static async Task InsertGameAsync(SqliteConnection con, int compId, int p1, int p2, int s1, int s2, TtRankedPlayer rp1, TtRankedPlayer rp2, DateTime tournamentDate)
        {
            var cmd = con.CreateCommand();

            cmd.CommandText = @"
        INSERT INTO games (
            competition_id, player1_id, player2_id, player1_sets, player2_sets, player1_keyName, player2_keyName,
            player1_points, player2_points, player1_PointsWithBonus, player2_PointsWithBonus, player1_age, player2_age,
            player1_place, player2_place)
        VALUES ($c, $p1, $p2, $s1, $s2, $k1, $k2, $pts1, $pts2, $pb1, $pb2, $age1, $age2, $pl1, $pl2)";

            cmd.Parameters.AddWithValue("$c", compId);
            cmd.Parameters.AddWithValue("$p1", p1);
            cmd.Parameters.AddWithValue("$p2", p2);
            cmd.Parameters.AddWithValue("$s1", s1);
            cmd.Parameters.AddWithValue("$s2", s2);
            cmd.Parameters.AddWithValue("$k1", rp1.KeyName);
            cmd.Parameters.AddWithValue("$k2", rp2.KeyName);
            cmd.Parameters.AddWithValue("$pts1", rp1.Points);
            cmd.Parameters.AddWithValue("$pts2", rp2.Points);
            cmd.Parameters.AddWithValue("$pb1", rp1.PointsWithBonus);
            cmd.Parameters.AddWithValue("$pb2", rp2.PointsWithBonus);
            cmd.Parameters.AddWithValue("$age1", AgeCalculator.GetAge(tournamentDate, rp1.BirthDate));
            cmd.Parameters.AddWithValue("$age2", AgeCalculator.GetAge(tournamentDate, rp2.BirthDate));
            cmd.Parameters.AddWithValue("$pl1", rp1.Place);
            cmd.Parameters.AddWithValue("$pl2", rp2.Place);

            await cmd.ExecuteNonQueryAsync();
        }

        // ====================================================================
        //  Tournament API calls
        // ====================================================================

        private async Task<List<TtCompetitionEventListItem>> GetCompetitionEventsAsync(
            string type, string? fromDate)
        {
            try
            {
                string url = $"{TournamentApiBase}/competition-events?type={type}";
                if (!string.IsNullOrWhiteSpace(fromDate)) url += $"&from_date={fromDate}";
                var res = await _http.GetFromJsonAsync<TtCompetitionEventsResponse>(url);

                return res?.competition_events ?? [];
            }
            catch 
            { 
                return []; 
            }
        }

        private async Task<TtEventResultResponse?> GetEventResultsAsync(int eventId, string? playingDate)
        {
            try
            {
                string url = $"{TournamentApiBase}/competition-event-results?competition_event_id={eventId}";
                if (!string.IsNullOrWhiteSpace(playingDate)) url += $"&playing_date={playingDate}";

                var resp = await _http.GetAsync(url);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(61_000);
                    _reqCount = 0; _windowStart = DateTime.Now;

                    return await GetEventResultsAsync(eventId, playingDate);
                }
                if (!resp.IsSuccessStatusCode) return null;

                return await resp.Content.ReadFromJsonAsync<TtEventResultResponse>();
            }
            catch 
            { 
                return null; 
            }
        }

        // ====================================================================
        //  Rate limiter
        // ====================================================================

        private async Task RateLimitAsync()
        {
            _reqCount++;
            var elapsed = DateTime.Now - _windowStart;
            if (_reqCount >= 90)
            {
                if (elapsed.TotalSeconds < 60)
                {
                    await Task.Delay((61 - (int)elapsed.TotalSeconds) * 1000);
                }

                _reqCount = 0; _windowStart = DateTime.Now;
            }
        }

        // ====================================================================
        //  Game collection
        // ====================================================================

        private static List<TtGroupGame> CollectGames(TtEventResultResponse ev)
        {
            var games = new List<TtGroupGame>();
            if (ev.nets == null) return games;

            // Standard: singles at top level of groups / elimination trees
            foreach (var net in ev.nets)
            {
                foreach (var g in net.groups?.SelectMany(gr => gr.games ?? []) ?? [])
                {
                    TryAdd(games, g);
                }
                foreach (var g in net.elimination_trees?.SelectMany(t => t.rounds ?? []).SelectMany(r => r.games ?? []) ?? [])
                {
                    TryAdd(games, g);
                }
            }

            if (games.Count > 0)
            {
                return games;
            }

            // Fallback: teams events — singles nested inside team-match games[]
            foreach (var net in ev.nets)
            {
                foreach (var tm in net.groups?.SelectMany(gr => gr.games ?? []) ?? [])
                {
                    if (tm.game_type == "teams" && tm.games != null)
                    {
                        foreach (var g in tm.games)
                        {
                            TryAdd(games, g);
                        }
                    }
                }

                foreach (var tm in net.elimination_trees?.SelectMany(t => t.rounds ?? []).SelectMany(r => r.games ?? []) ?? [])
                {
                    if (tm.game_type == "teams" && tm.games != null)
                    {
                        foreach (var g in tm.games)
                        {
                            TryAdd(games, g);

                        }
                    }
                }
            }

            return games;
        }

        private static void TryAdd(List<TtGroupGame> list, TtGroupGame g)
        {
            if (g.game_type != "singles") return;
            if (g.player1 == null || g.player2 == null) return;
            if (!int.TryParse(g.player1_score, out var s1)) return;
            if (!int.TryParse(g.player2_score, out var s2)) return;
            list.Add(new TtGroupGame
            {
                id = g.id,
                game_type = g.game_type,
                player1 = g.player1,
                player2 = g.player2,
                player1_score = s1.ToString(),
                player2_score = s2.ToString()
            });
        }

        // ====================================================================
        //  Helpers
        // ====================================================================

        /// <summary>
        /// Singles / non-season:
        ///   same names → use event name; event ≥ 30 chars → use event name; else "Comp — Event"
        /// </summary>
        private static string BuildTournamentName(string eventName, string? competitionName)
        {
            eventName = eventName.Trim();
            if (string.IsNullOrWhiteSpace(competitionName))
            {
                return eventName;
            }

            competitionName = competitionName.Trim();
            if (competitionName == eventName || eventName.Length >= 30)
            {
                return eventName;
            }

            return $"{competitionName} — {eventName}";
        }

        /// <summary>Season teams: "Comp — Event YYYY-MM-DD"</summary>
        private static string BuildSeasonTournamentName(string competitionName, string eventName, string dateStr)
        {
            competitionName = competitionName.Trim();
            eventName = eventName.Trim();

            return string.IsNullOrWhiteSpace(competitionName) ? $"{eventName} {dateStr}" : $"{competitionName} — {eventName} {dateStr}";
        }

        private static double ParseCoef(string? raw) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;

        private static string NormalizeKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "";
            }

            var nd = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in nd)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Replace(" ", "").Replace("-", "");
        }

        // ====================================================================
        //  Public: read import log dates (shown to all users on the page)
        // ====================================================================

        public async Task<(DateTime? SinglesDate, DateTime? TeamsDate)> GetImportDatesAsync()
        {
            try
            {
                await using var con = new SqliteConnection(_cs);
                await con.OpenAsync();
                await EnsureImportLogSchemaAsync(con);
                var log = await GetImportLogAsync(con);

                return (log.LastSinglesDate, log.LastTeamsDate);
            }
            catch 
            { 
                return (null, null); 
            }
        }
    }
}