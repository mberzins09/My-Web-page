namespace MartinsWeb.Models
{
    /// <summary>A country available to pick as a team when building predictions tournaments.</summary>
    public class Country
    {
        public int    Id        { get; set; }
        public string Name      { get; set; } = "";
        public string FlagEmoji { get; set; } = "";

        public ICollection<CountryRanking> Rankings { get; set; } = new List<CountryRanking>();
    }

    /// <summary>
    /// A country's world ranking in one sport for one year. Kept per-year so a tournament from an
    /// earlier year keeps showing the ranking that was current then, not today's.
    /// Sport uses the same strings as Tournament.PointsCalculationType ("Football", "Hockey", ...).
    /// </summary>
    public class CountryRanking
    {
        public int    Id        { get; set; }
        public int    CountryId { get; set; }
        public string Sport     { get; set; } = "";
        public int    Year      { get; set; }
        public int    Rank      { get; set; }

        public Country Country { get; set; } = null!;
    }
}
