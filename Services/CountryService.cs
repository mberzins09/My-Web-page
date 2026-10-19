using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    public class CountryService
    {
        private readonly AppDbContext _db;

        /// <summary>
        /// Sports a ranking can be recorded for. Kept separate from Tournament.PointsCalculationType
        /// (which also has calculator variants like "Football2") because a ranking is about the sport
        /// itself, not about which points formula a tournament uses.
        /// </summary>
        public static readonly string[] Sports =
            ["Football", "Hockey", "Volleyball", "Basketball", "TableTennis", "Floorball"];

        /// <summary>
        /// Maps a tournament's PointsCalculationType (which has scoring-formula variants like
        /// "Football2", "Hockey2") to the plain sport name rankings are stored under.
        /// </summary>
        public static string SportFor(string pointsCalculationType) => pointsCalculationType switch
        {
            "Football" or "Football2" or "Football3" => "Football",
            "Hockey" or "Hockey2"                     => "Hockey",
            var s when Sports.Contains(s)              => s,
            _                                           => "Football",
        };

        /// <summary>The sport to use for ranking lookups for this tournament - its own SportType when
        /// set, otherwise the historic guess from PointsCalculationType for older tournaments.</summary>
        public static string ResolveSport(Tournament tournament) =>
            !string.IsNullOrWhiteSpace(tournament.SportType) ? tournament.SportType : SportFor(tournament.PointsCalculationType);

        public CountryService(AppDbContext db) => _db = db;

        public async Task<List<Country>> GetAllAsync()
        {
            var list = await _db.Countries
                .Include(c => c.Rankings)
                .OrderBy(c => c.Name)
                .ToListAsync();

            // Self-heal anything saved before NormalizeFlag existed (e.g. a plain "LV" typed into
            // the flag box before this fix, still literally "LV" in the database).
            bool anyFixed = false;
            foreach (var c in list)
            {
                string normalized = NormalizeFlag(c.FlagEmoji);
                if (normalized != c.FlagEmoji)
                {
                    c.FlagEmoji = normalized;
                    anyFixed = true;
                }
            }
            if (anyFixed) await _db.SaveChangesAsync();

            return list;
        }

        public async Task<Country> CreateAsync(string name, string flagEmoji)
        {
            name = (name ?? "").Trim();
            flagEmoji = NormalizeFlag(flagEmoji);
            if (name.Length == 0) throw new ArgumentException("Name can't be empty.");

            var country = new Country { Name = name, FlagEmoji = flagEmoji };
            _db.Countries.Add(country);
            await _db.SaveChangesAsync();
            return country;
        }

        public async Task UpdateAsync(int id, string name, string flagEmoji)
        {
            var country = await _db.Countries.FindAsync(id) ?? throw new InvalidOperationException("Country not found.");
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Name can't be empty.");

            country.Name = name;
            country.FlagEmoji = NormalizeFlag(flagEmoji);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// If the admin typed a plain 2-letter country code (e.g. "LV", "lv") instead of pasting an
        /// actual flag character, converts it to the matching Unicode flag emoji. A flag emoji is
        /// just two Regional Indicator Symbols back to back, so "LV" becomes the same 🇱🇻 a real flag
        /// paste would give - typing the code is much easier than finding/copying the emoji itself.
        /// Anything else (an emoji already, a longer code, empty) is kept exactly as typed.
        /// </summary>
        public static string NormalizeFlag(string? raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length != 2 || !char.IsAsciiLetter(s[0]) || !char.IsAsciiLetter(s[1])) return s;

            const int RegionalIndicatorBase = 0x1F1E6;   // 🇦 = base + ('A' - 'A')
            char First(char c) => char.ToUpperInvariant(c);
            string Flag(char c) => char.ConvertFromUtf32(RegionalIndicatorBase + (First(c) - 'A'));

            return Flag(s[0]) + Flag(s[1]);
        }

        /// <summary>Deletes the country and every ranking it has (cascade).</summary>
        public async Task DeleteAsync(int id)
        {
            var country = await _db.Countries.FindAsync(id);
            if (country == null) return;

            _db.Countries.Remove(country);
            await _db.SaveChangesAsync();
        }

        /// <summary>Creates or overwrites the country's ranking for one sport/year.</summary>
        public async Task SetRankingAsync(int countryId, string sport, int year, int rank)
        {
            var existing = await _db.CountryRankings
                .FirstOrDefaultAsync(r => r.CountryId == countryId && r.Sport == sport && r.Year == year);

            if (existing != null)
            {
                existing.Rank = rank;
            }
            else
            {
                _db.CountryRankings.Add(new CountryRanking
                {
                    CountryId = countryId,
                    Sport     = sport,
                    Year      = year,
                    Rank      = rank
                });
            }

            await _db.SaveChangesAsync();
        }

        public async Task DeleteRankingAsync(int rankingId)
        {
            var ranking = await _db.CountryRankings.FindAsync(rankingId);
            if (ranking == null) return;

            _db.CountryRankings.Remove(ranking);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// "🇱🇻 Latvia (12)" - flag, name and, when a ranking exists for that sport/year, the rank in
        /// parentheses. Falls back gracefully when the country or the ranking isn't found, so callers
        /// don't need to null-check before formatting a team name.
        /// </summary>
        /// <summary>
        /// "🇱🇻 Latvia(12)" - flag, name and, when a ranking can be found, the rank in parentheses.
        /// Prefers the exact year; if that year has no ranking for this sport, falls back to the
        /// most recent ranking at or before that year (e.g. a 2027 tournament uses a country's 2026
        /// ranking if 2027 was never entered), and failing that, the earliest ranking available for
        /// any later year. Falls back gracefully when the country or any ranking isn't found, so
        /// callers don't need to null-check before formatting a team name.
        /// </summary>
        public static string Format(Country? country, string? sport, int? year)
        {
            if (country == null) return "";

            string flag = string.IsNullOrEmpty(country.FlagEmoji) ? "" : country.FlagEmoji + " ";
            var rank = FindBestRanking(country, sport, year);

            return rank != null ? $"{flag}{country.Name}({rank.Rank})" : $"{flag}{country.Name}";
        }

        private static CountryRanking? FindBestRanking(Country country, string? sport, int? year)
        {
            if (sport == null) return null;

            var candidates = country.Rankings.Where(r => r.Sport == sport).ToList();
            if (candidates.Count == 0) return null;
            if (year == null) return candidates.OrderByDescending(r => r.Year).First();   // most recent available

            var exact = candidates.FirstOrDefault(r => r.Year == year.Value);
            if (exact != null) return exact;

            // No exact year - use the most recent ranking at or before this year, if any.
            var atOrBefore = candidates.Where(r => r.Year <= year.Value).OrderByDescending(r => r.Year).FirstOrDefault();
            if (atOrBefore != null) return atOrBefore;

            // Nothing that old exists yet (e.g. only future years are set) - use the earliest available.
            return candidates.OrderBy(r => r.Year).FirstOrDefault();
        }
    }
}
