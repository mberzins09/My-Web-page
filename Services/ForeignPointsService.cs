using Microsoft.Data.Sqlite;
using System.Globalization;
using MartinsWeb.Models;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Gives foreign players (PlayerDB.Gender is null) points on the Latvian scale.
    ///
    /// Two lists are compared for one month:
    ///   * rankings1 (separate "output" database) - every player has points from your own Elo-style program
    ///   * PlayerDB (lgtf.sqlite)                 - players with a gender have the official Latvian Points
    ///
    /// Players that are in both lists (and have a gender) show how the two scales relate.
    /// That relation is stored as a coefficient per points range:  Points ≈ rankings1 points × coefficient.
    /// Foreign players then get their rankings1 points converted with the coefficient of their range.
    ///
    /// The converted points are treated as the foreigner's points at the START of that month. After
    /// saving, the month (and any later month) is replayed, so the games played since then move the
    /// foreigner's points like anyone else's. From then on every import keeps them up to date.
    ///
    /// Configuration (appsettings.json → ConnectionStrings):
    ///   "OutputConnection": "Data Source=output.sqlite"     (defaults to output.sqlite in the app folder)
    /// </summary>
    public class ForeignPointsService
    {
        private const int MinBinSize = 30;   // players needed to trust a coefficient
        private const int MaxBins    = 10;

        private readonly string _lgtfCs;
        private readonly string _outputCs;
        private readonly LgtfRankingService _ranking;

        public ForeignPointsService(IConfiguration config, LgtfRankingService ranking)
        {
            _ranking = ranking;
            _lgtfCs = config.GetConnectionString("LgtfConnection")
                      ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "lgtf.sqlite")}";
            _outputCs = config.GetConnectionString("OutputConnection")
                        ?? $"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "output.sqlite")}";
        }

        private record RankingRow(double Points, bool IsActive);
        private record PlayerRow(int Id, string Name, bool HasGender, int Points, bool IsActive, bool HasKey);
        private record Sample(double X, double Y);

        /// <param name="month">"yyyy-MM", the rankings1 month to use.</param>
        /// <param name="apply">false = calculate only and return the result without writing.</param>
        public async Task<ForeignPointsResult> AssignForeignPointsAsync(string month, bool apply)
        {
            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("Month must look like 2026-09.");

            var rankings = await LoadRankingsAsync(month);
            if (rankings.Count == 0)
                throw new InvalidOperationException($"rankings1 has no rows for {month}.");

            var players = await LoadPlayersAsync();
            var byId    = players.ToDictionary(p => p.Id);

            var result = new ForeignPointsResult
            {
                Month = month,
                RankingRowsWithoutPlayer = rankings.Keys.Count(id => !byId.ContainsKey(id)),
            };

            // ── 1. Calibration: players with gender that are in both lists ──────
            var samples = new List<Sample>();
            foreach (var p in players)
            {
                if (!p.HasGender || !p.IsActive || p.Points <= 0) continue;
                if (!rankings.TryGetValue(p.Id, out var rk) || !rk.IsActive || rk.Points <= 0) continue;
                samples.Add(new Sample(rk.Points, p.Points));
            }

            if (samples.Count < MinBinSize)
                throw new InvalidOperationException(
                    $"Only {samples.Count} players with a gender are in both lists for {month} - need at least {MinBinSize}.");

            result.Bins = BuildBins(samples);
            result.CalibrationPlayers = samples.Count;

            var errors = samples
                .Select(s => Math.Abs(Math.Round(s.X * CoefficientAt(result.Bins, s.X)) - s.Y))
                .ToList();
            result.MeanAbsError   = errors.Average();
            result.MedianAbsError = Median(errors);

            // ── 2. Foreign players → converted points ───────────────────────────
            var rows = new List<ForeignPointsRow>();
            foreach (var p in players)
            {
                if (p.HasGender || !p.HasKey) continue;

                if (!rankings.TryGetValue(p.Id, out var rk) || rk.Points <= 0)
                {
                    result.ForeignsWithoutRanking++;
                    continue;
                }

                rows.Add(new ForeignPointsRow
                {
                    PlayerId     = p.Id,
                    Name         = p.Name,
                    SourcePoints = rk.Points,
                    OldPoints    = p.Points,
                    NewPoints    = (int)Math.Round(rk.Points * CoefficientAt(result.Bins, rk.Points)),
                });
            }

            // Keep the order of the source scale: more rankings1 points never means fewer points.
            int floor = 0;
            foreach (var row in rows.OrderBy(r => r.SourcePoints))
            {
                if (row.NewPoints < floor) row.NewPoints = floor;
                floor = row.NewPoints;
            }

            result.Rows = [.. rows.OrderByDescending(r => r.SourcePoints)];
            result.ForeignsUpdated = rows.Count;

            // ── 3. Save, then replay so this month's games are applied ──────────
            if (apply && rows.Count > 0)
            {
                await using (var con = new SqliteConnection(_lgtfCs))
                {
                    await con.OpenAsync();

                    var ddl = con.CreateCommand();
                    ddl.CommandText = LgtfRankingService.ForeignDeltaTableSql;
                    await ddl.ExecuteNonQueryAsync();

                    await using var tr = await con.BeginTransactionAsync();

                    foreach (var row in rows)
                    {
                        var cmd = con.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tr;
                        cmd.CommandText = @"
                            UPDATE PlayerDB
                            SET    Points = $p, PointsWithBonus = $p
                            WHERE  Id = $id AND (Gender IS NULL OR Gender = '')";
                        cmd.Parameters.AddWithValue("$p",  row.NewPoints);
                        cmd.Parameters.AddWithValue("$id", row.PlayerId);
                        await cmd.ExecuteNonQueryAsync();

                        // The new value is the start of this month: forget what earlier replays did from here on.
                        var del = con.CreateCommand();
                        del.Transaction = (SqliteTransaction)tr;
                        del.CommandText = "DELETE FROM foreign_month_delta WHERE player_id = $id AND month >= $m";
                        del.Parameters.AddWithValue("$id", row.PlayerId);
                        del.Parameters.AddWithValue("$m",  month);
                        await del.ExecuteNonQueryAsync();
                    }

                    await tr.CommitAsync();
                }

                result.Applied = true;

                var (year, mon) = (int.Parse(month[..4]), int.Parse(month[5..7]));
                var months = await _ranking.GetCompetitionMonthsFromAsync(year, mon);
                if (months.Count > 0)
                {
                    await _ranking.RecalculateMonthsAsync(months, msg => result.RecalcLog.Add(msg));
                }
            }

            return result;
        }

        // ====================================================================
        //  Calibration
        // ====================================================================

        /// <summary>
        /// Sorts the samples by rankings1 points and cuts them into ranges with roughly equal
        /// player counts (so every range has enough players). The coefficient of a range is the
        /// median of Points / rankings1 points - the median keeps a few odd players from skewing it.
        /// </summary>
        private static List<CalibrationBin> BuildBins(List<Sample> samples)
        {
            var sorted = samples.OrderBy(s => s.X).ToList();
            int n        = sorted.Count;
            int binSize  = Math.Max(MinBinSize, (int)Math.Ceiling(n / (double)MaxBins));
            int binCount = Math.Max(1, n / binSize);   // the last range takes the remainder

            var bins = new List<CalibrationBin>();
            for (int b = 0; b < binCount; b++)
            {
                int start = b * binSize;
                int end   = b == binCount - 1 ? n : start + binSize;
                var slice = sorted.GetRange(start, end - start);

                bins.Add(new CalibrationBin
                {
                    MinX        = slice[0].X,
                    MaxX        = slice[^1].X,
                    CenterX     = Median(slice.Select(s => s.X).ToList()),
                    Coefficient = Median(slice.Select(s => s.Y / s.X).ToList()),
                    Count       = slice.Count,
                });
            }

            return bins;
        }

        /// <summary>
        /// Coefficient for a rankings1 value: linear blend between the two nearest range
        /// centers, so there are no jumps at range borders.
        /// Inside the outermost ranges (between the first/last center and the lowest/highest
        /// player seen) the trend of the two outermost ranges is continued - the top ranges are
        /// wide because few players are that strong, and a flat coefficient there would
        /// under-rate the strongest foreigners. Outside the observed players it stays constant.
        /// </summary>
        private static double CoefficientAt(List<CalibrationBin> bins, double x)
        {
            var first = bins[0];
            var last  = bins[^1];
            if (bins.Count == 1) return first.Coefficient;

            if (x < first.CenterX)
            {
                double span = bins[1].CenterX - first.CenterX;
                double slope = span > 0 ? (bins[1].Coefficient - first.Coefficient) / span : 0;
                double xe = Math.Max(x, first.MinX);
                return Math.Max(0, first.Coefficient + slope * (xe - first.CenterX));
            }

            if (x > last.CenterX)
            {
                var prev = bins[^2];
                double span = last.CenterX - prev.CenterX;
                double slope = span > 0 ? (last.Coefficient - prev.Coefficient) / span : 0;
                double xe = Math.Min(x, last.MaxX);
                return Math.Max(0, last.Coefficient + slope * (xe - last.CenterX));
            }

            for (int i = 0; i < bins.Count - 1; i++)
            {
                var a = bins[i];
                var b = bins[i + 1];
                if (x > b.CenterX) continue;

                double span = b.CenterX - a.CenterX;
                if (span <= 0) return b.Coefficient;

                double t = (x - a.CenterX) / span;
                return a.Coefficient + t * (b.Coefficient - a.Coefficient);
            }

            return last.Coefficient;
        }

        private static double Median(List<double> values)
        {
            var s = values.OrderBy(v => v).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        }

        // ====================================================================
        //  Data access
        // ====================================================================

        /// <summary>player_id → rankings1 row for the month (opened read-only).</summary>
        private async Task<Dictionary<int, RankingRow>> LoadRankingsAsync(string month)
        {
            var csb = new SqliteConnectionStringBuilder(_outputCs) { Mode = SqliteOpenMode.ReadOnly };
            if (!File.Exists(Path.GetFullPath(csb.DataSource)))
                throw new FileNotFoundException(
                    $"Output database not found: {Path.GetFullPath(csb.DataSource)}. Set ConnectionStrings:OutputConnection.");

            var result = new Dictionary<int, RankingRow>();
            await using var con = new SqliteConnection(csb.ToString());
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT player_id, points, is_active
                FROM   rankings1
                WHERE  substr(month, 1, 7) = $m";
            cmd.Parameters.AddWithValue("$m", month);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1)) continue;

                int    id     = Convert.ToInt32(r.GetValue(0));
                double points = Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture);
                bool   active = r.IsDBNull(2) || Convert.ToInt32(r.GetValue(2)) != 0;

                result[id] = new RankingRow(points, active);
            }

            return result;
        }

        private async Task<List<PlayerRow>> LoadPlayersAsync()
        {
            var result = new List<PlayerRow>();
            await using var con = new SqliteConnection(_lgtfCs);
            await con.OpenAsync();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT Id,
                       TRIM(COALESCE(Name, '') || ' ' || COALESCE(Surname, '')),
                       CASE WHEN Gender IS NOT NULL AND Gender != '' THEN 1 ELSE 0 END,
                       COALESCE(Points, 0),
                       COALESCE(IsActive, 0),
                       CASE WHEN COALESCE(KeyName, '') != '' THEN 1 ELSE 0 END
                FROM PlayerDB";

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                result.Add(new PlayerRow(
                    r.GetInt32(0), r.GetString(1),
                    r.GetInt32(2) == 1, r.GetInt32(3), r.GetInt32(4) == 1, r.GetInt32(5) == 1));
            }

            return result;
        }
    }
}
