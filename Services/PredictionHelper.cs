using MartinsWeb.Models;
using Microsoft.AspNetCore.Components;

namespace MartinsWeb.Services
{
    public static class PredictionHelper
    {
        public static DateTime LatviaTime => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Riga"));

        public static int StageOrder(string stage) => stage.ToLower() switch
        {
            var s when s.Contains("grupa") => 0,
            var s when s.Contains("group") => 0,
            "round of 32" => 1,
            "round of 16" => 2,
            "quarter-final" => 3,
            "semi-final" => 4,
            "final" => 5,
            _ => 99
        };

        public static List<Game> FilteredGames(List<Game>? matches, string? selectedStage)
        {
            var cutoff = LatviaTime.Date.AddDays(-1);
            var all = matches?.Where(g => selectedStage == null || g.Stage == selectedStage) ?? Enumerable.Empty<Game>();
            var upcoming = all.Where(g => g.GameDate.Date >= cutoff).OrderBy(g => g.GameDate).ThenBy(g => StageOrder(g.Stage));
            var past = all.Where(g => g.GameDate.Date < cutoff).OrderByDescending(g => g.GameDate);
            
            return upcoming.Concat(past).ToList();
        }

        public static int ParseScore(ChangeEventArgs e)
        {
            int.TryParse(e.Value?.ToString(), out var v);
            return Math.Max(0, Math.Min(20, v));
        }

        public static bool IsHockeyMode(Game match, string calcType) =>
            calcType == "Hockey" || calcType == "Hockey2" ||
            (calcType == "Football2"
                && !match.Stage.Contains("grupa", StringComparison.OrdinalIgnoreCase)
                && !match.Stage.Contains("group", StringComparison.OrdinalIgnoreCase));

        // Football2 playoff and Hockey types: OT allowed regardless of goal diff
        // Football (classic): OT never shown
        public static bool CanHaveOT(Game match, string calcType, int homeDiff)
        {
            if (calcType == "Hockey" || calcType == "Hockey2")
                return Math.Abs(homeDiff) == 1;          // hockey: only 1-goal diff
            if (calcType == "Football2")
                return !match.Stage.Contains("grupa", StringComparison.OrdinalIgnoreCase)
                    && !match.Stage.Contains("group", StringComparison.OrdinalIgnoreCase);
            return false;                                  // Football: never
        }

        public static string PointsBadgeClass(int pts, string calcType)
        {
            int max = calcType switch
            {
                "Hockey" => 10,
                "Hockey2" or "Football2" => 7,
                "Football" => 3,
                _ => 6
            };
            int pct = max > 0 ? pts * 100 / max : 0;
            return pct switch
            {
                100 => "bg-warning text-dark",
                >= 50 => "bg-success",
                >= 25 => "bg-info text-dark",
                > 0 => "bg-secondary",
                _ => "bg-danger"
            };
        }

        public static string PredictionDistribution(List<(User user, Prediction? pred, UserGroup? userGroup)> preds, Game match, string calcType)
        {
            var ps = preds.Where(p => p.pred != null).Select(p => p.pred!).ToList();
            if (!ps.Any()) return "";

            if (IsHockeyMode(match, calcType))
            {
                int homeReg = ps.Count(p => p.PredictedHomeScore > p.PredictedAwayScore && !p.PredictedIsOvertime);
                int homeOT = ps.Count(p => p.PredictedHomeScore > p.PredictedAwayScore && p.PredictedIsOvertime);
                int awayOT = ps.Count(p => p.PredictedHomeScore < p.PredictedAwayScore && p.PredictedIsOvertime);
                int awayReg = ps.Count(p => p.PredictedHomeScore < p.PredictedAwayScore && !p.PredictedIsOvertime);
                return $"{homeReg} : (OT - {homeOT} : {awayOT}) : {awayReg}";
            }
            else
            {
                int homeWin = ps.Count(p => p.PredictedHomeScore > p.PredictedAwayScore);
                int tie = ps.Count(p => p.PredictedHomeScore == p.PredictedAwayScore);
                int awayWin = ps.Count(p => p.PredictedHomeScore < p.PredictedAwayScore);
                return $"{homeWin} : {tie} : {awayWin}";
            }
        }

        public static string MostPopular(
            List<(User user, Prediction? pred, UserGroup? userGroup)> preds, Game match, string calcType)
        {
            var ps = preds.Where(p => p.pred != null).Select(p => p.pred!).ToList();
            if (!ps.Any()) return "";

            bool hockeyMode = IsHockeyMode(match, calcType);
            var groups = ps
                .GroupBy(p => hockeyMode
                    ? $"{p.PredictedHomeScore}:{p.PredictedAwayScore}{(p.PredictedIsOvertime ? " OT" : "")}"
                    : $"{p.PredictedHomeScore}:{p.PredictedAwayScore}")
                .Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ToList();

            int maxCount = groups.First().Count;
            if (maxCount == 1 && groups.Count > 1)
                return "Most Popular: Unpredictable game";

            return "Most Popular: " + string.Join(", ",
                groups.Where(g => g.Count == maxCount).Select(g => g.Key));
        }

        public static string ResolveCalcType(UserGroup? group, Tournament tournament) => group?.PointsCalculationType ?? tournament.PointsCalculationType;
    }
}
