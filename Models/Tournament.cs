namespace MartinsWeb.Models
{
    public class Tournament
    {
        public int Id { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public string PointsCalculationType { get; set; } = "Football";
        public int Year { get; set; } = DateTime.Today.Year;

        /// <summary>
        /// The sport this tournament's teams are ranked in ("Football", "Hockey", ...), explicitly
        /// set on creation rather than guessed from PointsCalculationType - a hockey-calculator
        /// tournament isn't necessarily an ice hockey tournament for ranking purposes. Null on older
        /// tournaments; CountryService.ResolveSport falls back to PointsCalculationType for those.
        /// </summary>
        public string? SportType { get; set; }

        /// <summary>
        /// Comma-separated Country.Id list of the countries taking part in this tournament. Null/empty
        /// means every country in the database is available when picking teams - the historic
        /// behaviour, kept as the default so older tournaments aren't affected.
        /// </summary>
        public string? ParticipatingCountryIds { get; set; }

        public ICollection<TournamentGroup> Groups { get; set; } = new List<TournamentGroup>();
        public ICollection<Game> Games { get; set; } = new List<Game>();
    }
}
