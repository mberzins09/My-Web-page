namespace MartinsWeb.Models
{
    /// <summary>Result of matching an old tournament's plain-text team names against the countries table.</summary>
    public class TeamMatchResult
    {
        public int GamesUpdated      { get; set; }   // games with at least one side matched
        public int SidesMatched      { get; set; }   // home/away slots matched, across all games
        public int GroupTeamsMatched { get; set; }
        public List<string> UnmatchedNames { get; set; } = [];   // distinct team-name text that found no country - useful for spotting typos/aliases
    }
}
