using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    /// <summary>
    /// Auto-advances playoff brackets. A game's Home/AwayTeam can hold more than one possible
    /// country before it's decided ("placeholder slot") - admin picks 2+ countries for that slot
    /// when the bracket isn't seeded yet. Once an earlier round's game between exactly those
    /// countries gets a final score, the slot resolves to the winner (or, for a Bronze/3rd-place
    /// game, the loser) and its text switches from "🇦/🇧" to "🇦 Team A(3)".
    ///
    /// Rounds are identified purely from the free-text Stage field (no explicit bracket links), so
    /// this only resolves a slot whose earlier-round pairing is unambiguous: exactly 2 candidate
    /// countries with exactly one earlier-round game (in a stage with a lower round order) between
    /// that same pair. A slot with 3+ candidates spanning two rounds back is left as flags only -
    /// there's no reliable single earlier game to resolve it from round labels alone.
    /// </summary>
    public class PlayoffService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public static char IdSeparator => ',';

        public static List<int> ParseIds(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split(IdSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(int.Parse)
                     .ToList();

        public static string FormatIds(IEnumerable<int> ids) => string.Join(IdSeparator, ids);

        /// <summary>
        /// Round order for a free-text Stage label, and whether it's a Bronze/3rd-place game (which
        /// advances the LOSER of its feeder games instead of the winner). -1 = not a recognised
        /// playoff stage (group games, or a label the classifier doesn't know), which never
        /// participates in auto-advance.
        /// </summary>
        public static (int Order, bool IsBronze) ClassifyStage(string? stage)
        {
            string s = (stage ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return (-1, false);

            // Checked most-specific-first: "Semi-Final" and "Quarter-Final" both contain "final".
            if (s.Contains("bronze") || s.Contains("3rd") || s.Contains("third place")) return (4, true);
            if (s.Contains("semi"))                                                     return (3, false);
            if (s.Contains("quarter") || s.Contains("round of 8"))                       return (2, false);
            if (s.Contains("round of 16"))                                               return (1, false);
            if (s.Contains("round of 32"))                                               return (0, false);
            if (s.Contains("final"))                                                     return (4, false);   // catch-all "Final"

            return (-1, false);
        }

        /// <summary>
        /// Re-checks every placeholder slot in the tournament and resolves the ones that now have a
        /// decisive earlier-round result. Safe to call after every score save - already-resolved
        /// slots and non-playoff games are left untouched. Returns a line per slot it resolved.
        /// </summary>
        public async Task<List<string>> ResolveAsync(int tournamentId)
        {
            var log = new List<string>();

            var games = await _db.Games
                .Where(g => g.TournamentId == tournamentId)
                .ToListAsync();

            var countries = await _db.Countries.Include(c => c.Rankings).ToListAsync();
            var byId = countries.ToDictionary(c => c.Id);

            var classified = games
                .Select(g => (Game: g, Round: ClassifyStage(g.Stage)))
                .Where(x => x.Round.Order >= 0)
                .ToList();

            bool changed = true;
            while (changed)   // one pass can unlock the next round, so keep going until nothing moves
            {
                changed = false;

                foreach (var item in classified)
                {
                    changed |= TryResolveSide(item.Game, isHome: true,  item.Round, classified, byId, log);
                    changed |= TryResolveSide(item.Game, isHome: false, item.Round, classified, byId, log);
                }
            }

            if (log.Count > 0)
                await _db.SaveChangesAsync();

            return log;
        }

        /// <summary>
        /// Resolves one side of one game. The candidate pool can span more than one earlier round
        /// (e.g. a Final's home slot starts as all 4 players from one half of the bracket): earlier
        /// decisive games between two still-alive candidates eliminate the loser one pair at a time,
        /// same as a real bracket, until exactly 2 candidates remain. The very last game between
        /// those final 2 decides the slot - by winner, except for a Bronze/3rd-place game, which
        /// takes the loser instead.
        /// </summary>
        private static bool TryResolveSide(
            Game game, bool isHome, (int Order, bool IsBronze) round,
            List<(Game Game, (int Order, bool IsBronze) Round)> classified,
            Dictionary<int, Country> byId, List<string> log)
        {
            string? idsRaw = isHome ? game.HomeCountryIds : game.AwayCountryIds;
            var alive = new HashSet<int>(ParseIds(idsRaw));
            if (alive.Count < 2) return false;   // already resolved (1 id) or empty (manual text)

            var earlierDecisive = classified
                .Where(x => x.Round.Order < round.Order)
                .Select(x => (Order: x.Round.Order, Game: x.Game, Home: SideIds(x.Game, true), Away: SideIds(x.Game, false), x.Game.HomeScore, x.Game.AwayScore))
                .Where(x => x.HomeScore != null && x.AwayScore != null && x.HomeScore != x.AwayScore)
                .Where(x => x.Home.Count == 1 && x.Away.Count == 1)
                .ToList();

            // Narrow down to 2, one round at a time (oldest first): within a round, an earlier decisive
            // game between two still-alive candidates removes its loser, same as one round of a real
            // bracket eliminates half the field. Rounds are processed strictly in order so a later
            // round's game (the actual decider between the last 2) is never mistaken for an elimination.
            foreach (var tierOrder in earlierDecisive.Select(x => x.Order).Distinct().OrderBy(o => o))
            {
                if (alive.Count <= 2) break;

                foreach (var d in earlierDecisive.Where(x => x.Order == tierOrder))
                {
                    if (alive.Count <= 2) break;

                    int h = d.Home[0], a = d.Away[0];
                    if (!alive.Contains(h) || !alive.Contains(a)) continue;

                    int loser = d.HomeScore > d.AwayScore ? a : h;
                    alive.Remove(loser);
                }
            }

            if (alive.Count != 2) return false;   // still too early, or a tie is blocking the pool from narrowing further

            var pair = alive.ToList();
            var decider = earlierDecisive.FirstOrDefault(d =>
                (d.Home[0] == pair[0] && d.Away[0] == pair[1]) ||
                (d.Home[0] == pair[1] && d.Away[0] == pair[0]));
            if (decider.Game == null) return false;   // the deciding game between these last 2 hasn't been played/entered yet

            bool homeWon = decider.HomeScore > decider.AwayScore;
            int homeId = decider.Home[0];
            int awayId = decider.Away[0];

            // Bronze takes the loser of its deciding game; every other stage takes the winner.
            int advancingId = round.IsBronze
                ? (homeWon ? awayId : homeId)
                : (homeWon ? homeId : awayId);

            if (!byId.TryGetValue(advancingId, out var country)) return false;

            string text = CountryService.Format(country, null, null);
            // Prefer the exact formatted text (with ranking) the deciding game already resolved to for
            // this country, if available - keeps the "(rank)" suffix consistent instead of re-deriving it.
            string? deciderText = advancingId == homeId ? decider.Game.HomeTeam : decider.Game.AwayTeam;
            if (!string.IsNullOrWhiteSpace(deciderText)) text = deciderText;

            if (isHome)
            {
                game.HomeTeam = text;
                game.HomeCountryIds = advancingId.ToString();
            }
            else
            {
                game.AwayTeam = text;
                game.AwayCountryIds = advancingId.ToString();
            }

            log.Add($"{game.Stage}: {(isHome ? "home" : "away")} slot resolved to {text}");
            return true;
        }

        private static List<int> SideIds(Game g, bool isHome) => ParseIds(isHome ? g.HomeCountryIds : g.AwayCountryIds);
    }
}
