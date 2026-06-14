namespace MartinsWeb.Models
{
    public class Game
    {
        public int Id { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime GameDate { get; set; }
        public string Stage { get; set; } = "";   // "Group A", "Semi-Final", etc.
        public string Group { get; set; } = "";   // kept for legacy

        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public bool IsOvertime { get; set; }   // Hockey: was the result decided in overtime?

        // NEW: full-time score before OT (only relevant for playoff games
        // where some groups predict only full time, e.g. Football / Football3)
        public int? HomeFullTimeScore { get; set; }
        public int? AwayFullTimeScore { get; set; }

        // Set when game was auto-generated from a TournamentGroup (batch insert)
        public int? GroupId { get; set; }
        public TournamentGroup? TournamentGroup { get; set; }

        // Direct tournament link — set on every game (both single & batch)
        public int? TournamentId { get; set; }
        public Tournament? Tournament { get; set; }

        public ICollection<Prediction> Predictions { get; set; } = [];
    }
}
