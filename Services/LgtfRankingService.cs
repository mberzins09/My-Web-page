using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    // ── View model for PlayerDB rows ─────────────────────────────────────────

    public class PlayerDbEntry
    {
        public int    Id              { get; set; }
        public int    Place           { get; set; }
        public int    Points          { get; set; }
        public int    PointsWithBonus { get; set; }
        public int    PointsChanged   { get; set; }
        public string Name            { get; set; } = "";
        public string Surname         { get; set; } = "";
        public string Gender          { get; set; } = "";
        public int    OverallPlace    { get; set; }
        public string BirthDate       { get; set; } = "";
        public bool   IsActive        { get; set; }
        public int?   NewId           { get; set; }
        public string KeyName         { get; set; } = "";
        public int    CalcPlace       { get; set; }
        public int    CalcOverallPlace { get; set; }
        public string FullName        => $"{Name} {Surname}";
    }

    /// <summary>
    /// Manages the PlayerDB table: page queries for the Rankings page,
    /// per-tournament recalculation, monthly API sync, and IsActive updates.
    ///
    /// Registration: builder.Services.AddScoped&lt;LgtfRankingService&gt;();
    /// </summary>
    public class LgtfRankingService
    {
        private readonly string     _cs;
        private readonly HttpClient _http;

        private const string TournamentApiKey  = "org_trJaxebjAq9bQjdkPb1PJONCO1Im8befEFv7w8Jr";
        private const string RankingOldApiUrl  = "https://www.lgtf.lv/api/getRanking";
        private const string RankingNewApiBase = "https://turniri.lgtf.lv/api/v1/";

        // Normalises c.start_date (ticks or yyyy-MM-dd) to yyyy-MM-dd text.
        private const string NormDate = @"
            CASE WHEN CAST(start_date AS INTEGER) > 10000000000
                 THEN date('1970-01-01', '+' || ((CAST(start_date AS INTEGER) - 621355968000000000) / 10000000) || ' seconds')
                 ELSE start_date END";

        public bool IsRecalculating { get; private set; }

        public LgtfRankingService(IConfiguration config, IHttpClientFactory httpFactory)
        {
            _cs = config.GetConnectionString("LgtfConnection")
                  ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
            _http = httpFactory.CreateClient("lgtf");
            _http.DefaultRequestHeaders.Add("x-api-key", TournamentApiKey);
            _http.DefaultRequestHeaders.Add("Accept",    "application/json");
            _http.Timeout = TimeSpan.FromSeconds(90);
        }

        // ====================================================================
        //  Rankings page: paged / searched query
        // ====================================================================

        /// <summary>
        /// Returns a page of PlayerDB entries sorted by PointsWithBonus DESC.
        /// When <paramref name="search"/> is non-empty, pagination is ignored
        /// and all matching rows are returned.
        /// </summary>
        public async Task<(List<PlayerDbEntry> Players, int Total)> GetPlayersPageAsync(
            string? gender, bool showInactive, int page, int pageSize, string? search)
        {
            await EnsurePlayerDbSchemaAsync();
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            // ── Shared WHERE conditions ─────────────────────────────────────
            var conditions = new List<string> { "Gender IS NOT NULL AND Gender != ''" };
            if (!showInactive)
                conditions.Add("IsActive = 1");
            if (!string.IsNullOrWhiteSpace(gender) && gender != "all")
                conditions.Add("LOWER(Gender) = LOWER($gender)");

            string where = "WHERE " + string.Join(" AND ", conditions);

            // ── Search mode (no pagination) ─────────────────────────────────
            if (!string.IsNullOrWhiteSpace(search))
            {
                var cmd = con.CreateCommand();
                cmd.CommandText = $@"
                    SELECT Id, Place, Points, PointsWithBonus, PointsChanged,
                           Name, Surname, Gender, OverallPlace, BirthDate, IsActive, NewId, KeyName,
                           COALESCE(calcPlace, 0), COALESCE(calcOverallPlace, 0)
                    FROM PlayerDB
                    {where}
                      AND (Name LIKE $s OR Surname LIKE $s OR (Name || ' ' || Surname) LIKE $s)
                    ORDER BY PointsWithBonus DESC";
                if (!string.IsNullOrWhiteSpace(gender) && gender != "all")
                    cmd.Parameters.AddWithValue("$gender", gender);
                cmd.Parameters.AddWithValue("$s", $"%{search}%");

                var list = new List<PlayerDbEntry>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(ReadPlayerDb(r));
                return (list, list.Count);
            }

            // ── Count ───────────────────────────────────────────────────────
            var countCmd = con.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM PlayerDB {where}";
            if (!string.IsNullOrWhiteSpace(gender) && gender != "all")
                countCmd.Parameters.AddWithValue("$gender", gender);
            int total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            // ── Paged result ────────────────────────────────────────────────
            var pageCmd = con.CreateCommand();
            pageCmd.CommandText = $@"
                SELECT Id, Place, Points, PointsWithBonus, PointsChanged,
                       Name, Surname, Gender, OverallPlace, BirthDate, IsActive, NewId, KeyName,
                       COALESCE(calcPlace, 0), COALESCE(calcOverallPlace, 0)
                FROM PlayerDB
                {where}
                ORDER BY PointsWithBonus DESC
                LIMIT $limit OFFSET $offset";
            if (!string.IsNullOrWhiteSpace(gender) && gender != "all")
                pageCmd.Parameters.AddWithValue("$gender", gender);
            pageCmd.Parameters.AddWithValue("$limit",  pageSize);
            pageCmd.Parameters.AddWithValue("$offset", page * pageSize);

            var players = new List<PlayerDbEntry>();
            await using var pr = await pageCmd.ExecuteReaderAsync();
            while (await pr.ReadAsync()) players.Add(ReadPlayerDb(pr));

            return (players, total);
        }

        // ====================================================================
        //  Called by LgtfImportService after each tournament is imported
        // ====================================================================

        /// <summary>
        /// If this is the first competition of its calendar month in the DB,
        /// syncs PlayerDB from the official API for that month first (resetting
        /// Points/PointsWithBonus and IsActive), then replays this tournament's
        /// games against the current PlayerDB state.
        /// </summary>
        public async Task RecalculateTournamentAsync(int compId, DateTime compDate, Action<string> progress)
        {
            if (await IsFirstTournamentOfMonthAsync(compId, compDate))
            {
                progress("  📊 First tournament of month — syncing PlayerDB from official rankings…");
                await SyncFromApiAsync(compDate.ToString("yyyy-MM"), progress);
            }

            progress($"  📊 Recalculating PlayerDB for competition {compId}…");
            await RunRecalculationAsync(compId, compDate, progress);
        }

        // ====================================================================
        //  Admin: "Recalculate Month" button
        // ====================================================================

        /// <summary>
        /// Resets PlayerDB to official API values for <paramref name="year"/>/<paramref name="month"/>,
        /// then replays every competition in that month in chronological order.
        /// </summary>
        public async Task RecalculateMonthAsync(int year, int month, Action<string> progress)
        {
            if (IsRecalculating) { progress("⚠️ Already recalculating."); return; }
            IsRecalculating = true;
            try
            {
                string yearMonth = $"{year:D4}-{month:D2}";
                var comps = await GetCompetitionsForMonthAsync(year, month);

                if (!comps.Any())
                {
                    progress($"No competitions found for {yearMonth}.");
                    return;
                }

                progress($"Found {comps.Count} competition(s) for {yearMonth}. Syncing from official rankings…");
                await SyncFromApiAsync(yearMonth, progress);

                int i = 0;
                foreach (var (compId, name, compDate) in comps)
                {
                    progress($"\n[{++i}/{comps.Count}] {name} ({compDate:yyyy-MM-dd})");
                    await RunRecalculationAsync(compId, compDate, progress);
                }

                progress("\n✅ Month recalculation complete.");
            }
            finally { IsRecalculating = false; }
        }

        // ====================================================================
        //  Private: first-of-month detection
        // ====================================================================

        private async Task<bool> IsFirstTournamentOfMonthAsync(int compId, DateTime compDate)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT COUNT(*) FROM competitions
                WHERE id != $cid
                  AND strftime('%Y-%m', ({NormDate})) = $ym";
            cmd.Parameters.AddWithValue("$cid", compId);
            cmd.Parameters.AddWithValue("$ym",  compDate.ToString("yyyy-MM"));

            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0;
        }

        // ====================================================================
        //  Private: load all competitions for a month
        // ====================================================================

        private async Task<List<(int Id, string Name, DateTime Date)>> GetCompetitionsForMonthAsync(int year, int month)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, name, ({NormDate}) AS nd
                FROM competitions
                WHERE CAST(strftime('%Y', ({NormDate})) AS INTEGER) = $year
                  AND CAST(strftime('%m', ({NormDate})) AS INTEGER) = $month
                ORDER BY nd ASC, id ASC";
            cmd.Parameters.AddWithValue("$year",  year);
            cmd.Parameters.AddWithValue("$month", month);

            var result = new List<(int, string, DateTime)>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                int      id   = r.GetInt32(0);
                string   name = r.GetString(1);
                DateTime dt   = DateTime.TryParse(r.IsDBNull(2) ? "" : r.GetString(2), out var d) ? d : DateTime.MinValue;
                result.Add((id, name, dt));
            }

            return result;
        }

        // ====================================================================
        //  Private: sync PlayerDB from official API
        // ====================================================================

        private async Task SyncFromApiAsync(string yearMonth, Action<string> progress)
        {
            progress($"  Fetching male rankings for {yearMonth}…");
            var males = await FetchRankingFromApiAsync("male", "virietis", yearMonth);
            progress($"  Got {males.Count} male player(s).");

            await Task.Delay(350);

            progress($"  Fetching female rankings for {yearMonth}…");
            var females = await FetchRankingFromApiAsync("female", "sieviete", yearMonth);
            progress($"  Got {females.Count} female player(s).");

            if (males.Count == 0 && females.Count == 0)
            {
                progress("  ⚠️ No ranking data from API — skipping sync.");
                return;
            }

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            await using var tr  = await con.BeginTransactionAsync();

            // Mark all players inactive; API pass will re-activate those present
            var deact = con.CreateCommand(); deact.Transaction = (SqliteTransaction)tr;
            deact.CommandText = "UPDATE PlayerDB SET IsActive = 0";
            await deact.ExecuteNonQueryAsync();

            // Load existing KeyName → Id map
            var existing = new Dictionary<string, int>(StringComparer.Ordinal);
            var sel = con.CreateCommand(); sel.Transaction = (SqliteTransaction)tr;
            sel.CommandText = "SELECT Id, KeyName FROM PlayerDB WHERE KeyName IS NOT NULL AND KeyName != ''";
            await using (var r = await sel.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    if (!r.IsDBNull(1))
                        existing[r.GetString(1)] = r.GetInt32(0);

            // Process male and female lists
            foreach (var (apiList, genderTag) in new[] { (males, "male"), (females, "female") })
            {
                foreach (var p in apiList)
                {
                    if (string.IsNullOrEmpty(p.KeyName)) continue;

                    if (existing.TryGetValue(p.KeyName, out int dbId))
                    {
                        // Update existing
                        var upd = con.CreateCommand(); upd.Transaction = (SqliteTransaction)tr;
                        upd.CommandText = @"UPDATE PlayerDB SET
                            Points = $pts, PointsWithBonus = $pwb,
                            IsActive = 1, Place = $place, Gender = $gender
                            WHERE Id = $id";
                        upd.Parameters.AddWithValue("$pts",    p.Points);
                        upd.Parameters.AddWithValue("$pwb",    p.PointsWithBonus);
                        upd.Parameters.AddWithValue("$place",  p.Place);
                        upd.Parameters.AddWithValue("$gender", genderTag);
                        upd.Parameters.AddWithValue("$id",     dbId);
                        await upd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Insert new — look up name/surname and players.id from the players table
                        var (pname, psurname, playerId) = await GetPlayerInfoAsync(con, (SqliteTransaction)tr, p.KeyName);

                        var ins = con.CreateCommand(); ins.Transaction = (SqliteTransaction)tr;
                        ins.CommandText = @"INSERT INTO PlayerDB
                            (Place, Points, PointsWithBonus, PointsChanged, Name, Surname,
                             Gender, BirthDate, IsActive, NewId, KeyName, OverallPlace)
                            VALUES ($place, $pts, $pwb, 0, $name, $sn, $gender, $bd, 1, $newid, $kn, 0)";
                        ins.Parameters.AddWithValue("$place",  p.Place);
                        ins.Parameters.AddWithValue("$pts",    p.Points);
                        ins.Parameters.AddWithValue("$pwb",    p.PointsWithBonus);
                        ins.Parameters.AddWithValue("$name",   pname);
                        ins.Parameters.AddWithValue("$sn",     psurname);
                        ins.Parameters.AddWithValue("$gender", genderTag);
                        ins.Parameters.AddWithValue("$bd",     p.BirthDate);
                        ins.Parameters.AddWithValue("$newid",  playerId.HasValue ? (object)playerId.Value : DBNull.Value);
                        ins.Parameters.AddWithValue("$kn",     p.KeyName);
                        await ins.ExecuteNonQueryAsync();

                        // Track new id for subsequent lookups in the same sync
                        var getid = con.CreateCommand(); getid.Transaction = (SqliteTransaction)tr;
                        getid.CommandText = "SELECT last_insert_rowid()";
                        existing[p.KeyName] = Convert.ToInt32(await getid.ExecuteScalarAsync());
                    }
                }
            }

            await tr.CommitAsync();
            await RecalculatePlacesAsync();
            await RecalculateCalcPlacesAsync();

            progress($"  ✅ Sync done. {males.Count + females.Count} player(s) processed.");
        }

        // ====================================================================
        //  Private: recalculate one tournament against current PlayerDB state
        // ====================================================================

        private async Task RunRecalculationAsync(int compId, DateTime compDate, Action<string> progress)
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            // Load competition coefficient
            double coef = 1.0;
            var coefCmd = con.CreateCommand();
            coefCmd.CommandText = "SELECT coef FROM competitions WHERE id = $id";
            coefCmd.Parameters.AddWithValue("$id", compId);
            var coefVal = await coefCmd.ExecuteScalarAsync();
            if (coefVal != null && coefVal != DBNull.Value) coef = Convert.ToDouble(coefVal);

            // Load all PlayerDB into working dict: KeyName → mutable state
            // COALESCE guards against NULL Points / PointsWithBonus / IsActive / Place
            var dict = new Dictionary<string, PlayerWorkState>(StringComparer.Ordinal);
            var loadCmd = con.CreateCommand();
            loadCmd.CommandText = @"
                SELECT Id, KeyName,
                       COALESCE(Points, 0), COALESCE(PointsWithBonus, 0),
                       COALESCE(IsActive, 0), COALESCE(Place, 0)
                FROM PlayerDB
                WHERE KeyName IS NOT NULL AND KeyName != ''";
            await using (var r = await loadCmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    string kn  = r.GetString(1);
                    int    pts = r.GetInt32(2);
                    int    pwb = r.GetInt32(3);
                    dict[kn] = new PlayerWorkState
                    {
                        DbId        = r.GetInt32(0),
                        Points      = pts,
                        BonusPoints = pwb - pts,  // preserved across recalculation
                        WasActive   = r.GetInt32(4) == 1,
                        Place       = r.GetInt32(5),
                        Participated = false
                    };
                }
            }

            // Load birth dates for age calculation
            var birthDates = new Dictionary<int, string>();
            var bdCmd = con.CreateCommand();
            bdCmd.CommandText = "SELECT id, COALESCE(birth_date, '') FROM players";
            await using (var r = await bdCmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    birthDates[r.GetInt32(0)] = r.GetString(1);

            // Load games (include id and player ids for games table update)
            var games = new List<(int gid, string k1, string k2, int s1, int s2, int p1id, int p2id)>();
            var gamesCmd = con.CreateCommand();
            gamesCmd.CommandText = @"
                SELECT id, player1_keyName, player2_keyName,
                       player1_sets, player2_sets, player1_id, player2_id
                FROM games
                WHERE competition_id = $cid
                  AND player1_keyName != '' AND player2_keyName != ''";
            gamesCmd.Parameters.AddWithValue("$cid", compId);
            await using (var r = await gamesCmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    games.Add((r.GetInt32(0), r.GetString(1), r.GetString(2),
                               r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)));

            if (games.Count == 0)
            {
                progress($"  ⚠️ No games with key names for competition {compId} — skipping.");
                return;
            }

            // Process games: snapshot pre-game state for games table, then apply deltas
            var gameTableUpdates = new List<(int gid, int pts1, int pts2, int pwb1, int pwb2,
                                             int age1, int age2, int place1, int place2)>();

            foreach (var (gid, k1, k2, s1, s2, p1id, p2id) in games)
            {
                bool f1 = dict.TryGetValue(k1, out var st1);
                bool f2 = dict.TryGetValue(k2, out var st2);

                int  pts1  = f1 ? st1!.Points : 0;
                int  pts2  = f2 ? st2!.Points : 0;
                bool p1Win = s1 > s2;

                // Snapshot current state BEFORE applying delta → goes into games table
                gameTableUpdates.Add((gid,
                    pts1, pts2,
                    pts1 + (f1 ? Math.Max(0, st1!.BonusPoints) : 0),
                    pts2 + (f2 ? Math.Max(0, st2!.BonusPoints) : 0),
                    CalcAge(birthDates.GetValueOrDefault(p1id, ""), compDate),
                    CalcAge(birthDates.GetValueOrDefault(p2id, ""), compDate),
                    f1 ? st1!.Place : 0,
                    f2 ? st2!.Place : 0));

                // Apply delta (only when both players have points)
                if (pts1 > 0 && pts2 > 0)
                {
                    int d1 = RatingCalculator.Calculate(pts1, pts2,  p1Win, coef);
                    int d2 = RatingCalculator.Calculate(pts2, pts1, !p1Win, coef);
                    if (f1) st1!.Points += d1;
                    if (f2) st2!.Points += d2;
                }

                if (f1) st1!.Participated = true;
                if (f2) st2!.Participated = true;
            }

            // Persist everything in one transaction
            await using var tr = await con.BeginTransactionAsync();

            // Write PlayerDB-sourced points into the games table
            foreach (var (gid, p1p, p2p, p1wb, p2wb, p1a, p2a, p1pl, p2pl) in gameTableUpdates)
            {
                var upg = con.CreateCommand(); upg.Transaction = (SqliteTransaction)tr;
                upg.CommandText = @"UPDATE games SET
                    player1_points=$p1p,  player2_points=$p2p,
                    player1_PointsWithBonus=$p1wb, player2_PointsWithBonus=$p2wb,
                    player1_age=$p1a,   player2_age=$p2a,
                    player1_place=$p1pl, player2_place=$p2pl,
                    ranking_data_filled=1
                    WHERE id=$id";
                upg.Parameters.AddWithValue("$p1p",  p1p);  upg.Parameters.AddWithValue("$p2p",  p2p);
                upg.Parameters.AddWithValue("$p1wb", p1wb); upg.Parameters.AddWithValue("$p2wb", p2wb);
                upg.Parameters.AddWithValue("$p1a",  p1a);  upg.Parameters.AddWithValue("$p2a",  p2a);
                upg.Parameters.AddWithValue("$p1pl", p1pl); upg.Parameters.AddWithValue("$p2pl", p2pl);
                upg.Parameters.AddWithValue("$id",   gid);
                await upg.ExecuteNonQueryAsync();
            }

            // Update PlayerDB for all participants
            int updated = 0;
            foreach (var (_, state) in dict.Where(kvp => kvp.Value.Participated))
            {
                int newPts = Math.Max(0, state.Points);
                int newPwb = newPts + Math.Max(0, state.BonusPoints);

                var upd = con.CreateCommand(); upd.Transaction = (SqliteTransaction)tr;
                upd.CommandText = @"UPDATE PlayerDB
                    SET Points = $pts, PointsWithBonus = $pwb, IsActive = 1
                    WHERE Id = $id";
                upd.Parameters.AddWithValue("$pts", newPts);
                upd.Parameters.AddWithValue("$pwb", newPwb);
                upd.Parameters.AddWithValue("$id",  state.DbId);
                await upd.ExecuteNonQueryAsync();
                updated++;
            }

            await tr.CommitAsync();
            progress($"  ✅ {updated} player(s) updated.");
            await RecalculateCalcPlacesAsync();
        }

        private static int CalcAge(string? bd, DateTime at)
        {
            if (string.IsNullOrWhiteSpace(bd) || !DateTime.TryParse(bd, out var b)) return 0;
            int a = at.Year - b.Year;
            if (b.AddYears(a) > at) a--;
            return a < 0 ? 0 : a;
        }

        // ====================================================================
        //  Private: recalculate Place and OverallPlace for all active players
        // ====================================================================

        private async Task RecalculatePlacesAsync()
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            // Fetch all active players ordered by PointsWithBonus DESC
            var all = new List<(int id, string gender)>();
            var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT Id, LOWER(COALESCE(Gender,''))
                                FROM PlayerDB WHERE IsActive = 1 AND Gender IS NOT NULL
                                ORDER BY PointsWithBonus DESC";
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    all.Add((r.GetInt32(0), r.GetString(1)));

            await using var tr = await con.BeginTransactionAsync();

            // Overall place
            int op = 0;
            foreach (var (id, _) in all)
            {
                op++;
                var u = con.CreateCommand(); u.Transaction = (SqliteTransaction)tr;
                u.CommandText = "UPDATE PlayerDB SET OverallPlace = $p WHERE Id = $id";
                u.Parameters.AddWithValue("$p",  op);
                u.Parameters.AddWithValue("$id", id);
                await u.ExecuteNonQueryAsync();
            }

            // Gender-specific place
            foreach (var g in new[] { "male", "female" })
            {
                int gp = 0;
                foreach (var (id, gender) in all.Where(x => x.gender == g))
                {
                    gp++;
                    var u = con.CreateCommand(); u.Transaction = (SqliteTransaction)tr;
                    u.CommandText = "UPDATE PlayerDB SET Place = $p WHERE Id = $id";
                    u.Parameters.AddWithValue("$p",  gp);
                    u.Parameters.AddWithValue("$id", id);
                    await u.ExecuteNonQueryAsync();
                }
            }

            await tr.CommitAsync();
        }

        /// <summary>
        /// Recalculates calcPlace (gender-specific) and calcOverallPlace
        /// based on current PointsWithBonus, active players only.
        /// Called after every tournament recalculation.
        /// </summary>
        private async Task RecalculateCalcPlacesAsync()
        {
            await EnsurePlayerDbSchemaAsync();

            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();

            var all = new List<(int id, string gender)>();
            var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT Id, LOWER(COALESCE(Gender,''))
                                FROM PlayerDB WHERE IsActive = 1 AND Gender IS NOT NULL
                                ORDER BY COALESCE(PointsWithBonus,0) DESC";
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    all.Add((r.GetInt32(0), r.GetString(1)));

            await using var tr = await con.BeginTransactionAsync();

            // calcOverallPlace
            int op = 0;
            foreach (var (id, _) in all)
            {
                op++;
                var u = con.CreateCommand(); u.Transaction = (SqliteTransaction)tr;
                u.CommandText = "UPDATE PlayerDB SET calcOverallPlace = $p WHERE Id = $id";
                u.Parameters.AddWithValue("$p",  op);
                u.Parameters.AddWithValue("$id", id);
                await u.ExecuteNonQueryAsync();
            }

            // calcPlace (gender-specific)
            foreach (var g in new[] { "male", "female" })
            {
                int gp = 0;
                foreach (var (id, gender) in all.Where(x => x.gender == g))
                {
                    gp++;
                    var u = con.CreateCommand(); u.Transaction = (SqliteTransaction)tr;
                    u.CommandText = "UPDATE PlayerDB SET calcPlace = $p WHERE Id = $id";
                    u.Parameters.AddWithValue("$p",  gp);
                    u.Parameters.AddWithValue("$id", id);
                    await u.ExecuteNonQueryAsync();
                }
            }

            await tr.CommitAsync();
        }

        /// <summary>
        /// Idempotent — adds calcPlace and calcOverallPlace columns if they don't exist yet.
        /// </summary>
        private async Task EnsurePlayerDbSchemaAsync()
        {
            await using var con = new SqliteConnection(_cs);
            await con.OpenAsync();
            foreach (var sql in new[]
            {
                "ALTER TABLE PlayerDB ADD COLUMN calcPlace        INTEGER DEFAULT 0",
                "ALTER TABLE PlayerDB ADD COLUMN calcOverallPlace INTEGER DEFAULT 0",
            })
            {
                try { var c = con.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
                catch { /* column already exists — safe to ignore */ }
            }
        }

        // ====================================================================
        //  Private: API fetching (mirrors LgtfImportService logic)
        // ====================================================================

        private async Task<List<TtRankedPlayer>> FetchRankingFromApiAsync(
            string genderNew, string genderOld, string yearMonth)
        {
            if (!DateTime.TryParseExact(yearMonth, "yyyy-MM",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return [];

            // Try new API
            try
            {
                string url = $"{RankingNewApiBase}ranking-list?ranking_id=2&gender={genderNew}&year={dt.Year}&month={dt.Month}";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<TtRankingNewResponse>();
                    if (data?.Players?.Count > 0)
                        return [.. data.Players
                            .Where(p => p.Name != null && p.Surname != null)
                            .Select(p => new TtRankedPlayer
                            {
                                KeyName         = NormalizeKey(p.Name! + p.Surname!),
                                Place           = p.Rank,
                                Points          = int.TryParse(p.Points,          out var pts) ? pts : 0,
                                PointsWithBonus = int.TryParse(p.PointsWithBonus, out var wb)  ? wb  : 0,
                                BirthDate       = p.BirthDate ?? ""
                            })];
                }
            }
            catch { }

            // Fallback: old API
            try
            {
                var body = new { date = dt.ToString("yyyy-MM-01"), gender = genderOld, year = dt.Year.ToString() };
                var resp = await _http.PostAsJsonAsync(RankingOldApiUrl, body);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<TtRankingOldResponse>();
                    if (data?.Players?.Count > 0)
                        return [.. data.Players
                            .Where(p => p.Name != null && p.Surname != null)
                            .Select(p => new TtRankedPlayer
                            {
                                KeyName         = NormalizeKey(p.Name! + p.Surname!),
                                Place           = p.Place,
                                Points          = p.Points,
                                PointsWithBonus = p.PointsWithBonus,
                                BirthDate       = p.BirthDate ?? ""
                            })];
                }
            }
            catch { }

            return [];
        }

        // ====================================================================
        //  Private: DB helpers
        // ====================================================================

        private static async Task<(string name, string surname, int? playerId)> GetPlayerInfoAsync(
            SqliteConnection con, SqliteTransaction tr, string keyName)
        {
            var cmd = con.CreateCommand(); cmd.Transaction = tr;
            cmd.CommandText = "SELECT id, name, surname FROM players WHERE key_name = $k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", keyName);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
                return (r.IsDBNull(1) ? "" : r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        r.GetInt32(0));
            return ("", "", null);
        }

        private static PlayerDbEntry ReadPlayerDb(SqliteDataReader r) => new()
        {
            Id               = r.GetInt32(0),
            Place            = r.IsDBNull(1)  ? 0   : r.GetInt32(1),
            Points           = r.IsDBNull(2)  ? 0   : r.GetInt32(2),
            PointsWithBonus  = r.IsDBNull(3)  ? 0   : r.GetInt32(3),
            PointsChanged    = r.IsDBNull(4)  ? 0   : r.GetInt32(4),
            Name             = r.IsDBNull(5)  ? ""  : r.GetString(5),
            Surname          = r.IsDBNull(6)  ? ""  : r.GetString(6),
            Gender           = r.IsDBNull(7)  ? ""  : r.GetString(7),
            OverallPlace     = r.IsDBNull(8)  ? 0   : r.GetInt32(8),
            BirthDate        = r.IsDBNull(9)  ? ""  : r.GetString(9),
            IsActive         = !r.IsDBNull(10) && r.GetInt32(10) == 1,
            NewId            = r.IsDBNull(11) ? null : r.GetInt32(11),
            KeyName          = r.IsDBNull(12) ? ""  : r.GetString(12),
            CalcPlace        = r.IsDBNull(13) ? 0   : r.GetInt32(13),
            CalcOverallPlace = r.IsDBNull(14) ? 0   : r.GetInt32(14),
        };

        private static string NormalizeKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var nd = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in nd)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Replace(" ", "").Replace("-", "");
        }

        // ── Mutable state per player during recalculation ────────────────────

        private sealed class PlayerWorkState
        {
            public int  DbId         { get; set; }
            public int  Points       { get; set; }
            public int  BonusPoints  { get; set; }
            public int  Place        { get; set; }
            public bool WasActive    { get; set; }
            public bool Participated { get; set; }
        }
    }
}
