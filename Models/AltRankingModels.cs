namespace MartinsWeb.Models
{
    /// <summary>One row of the alternative (RankingEngine2 / Elo-based) ranking, for display.</summary>
    public class AltPlayerRankingRow
    {
        public int     PlayerId  { get; set; }
        public string  Name      { get; set; } = "";
        public string  Surname   { get; set; } = "";
        public string? Gender    { get; set; }
        public int     Points    { get; set; }
        public bool    IsActive  { get; set; }
        public int     Place     { get; set; }
    }

    public class AltTtPlayer
    {
        public int    Id      { get; set; }
        public string Name    { get; set; } = "";
        public string Surname { get; set; } = "";
        public string? Gender { get; set; }
        public string FullName => $"{Name} {Surname}";
    }

    /// <summary>One tournament, for a specific player, in the alternative ranking.</summary>
    public class AltTtCompetition
    {
        public int      Id         { get; set; }
        public string   Name       { get; set; } = "";
        public DateTime Date       { get; set; }
        public double   Coef       { get; set; }
        public int      GamesCount { get; set; }
        public int      Wins       { get; set; }
        public int      Losses     { get; set; }

        public int    PointsBefore      { get; set; }   // entering the tournament
        public int?   Correction        { get; set; }   // non-null only if the engine adjusted the player's rating before this tournament's games
        public bool   IsFirstTournament { get; set; }
        public int?   InitialRating     { get; set; }    // only set when IsFirstTournament
        public int    PointsAfter       { get; set; }

        public int Year => Date.Year;
        public int RatingDiffTotal => PointsAfter - PointsBefore;
    }

    public class AltTtGameSide
    {
        public int    PlayerId     { get; set; }
        public string Name         { get; set; } = "";
        public string Surname      { get; set; } = "";
        public int    Sets         { get; set; }
        public int    PointsBefore { get; set; }
        public int    PointsAfter  { get; set; }
        public bool   IsWinner     { get; set; }
        public string FullName => $"{Name} {Surname}";
        public int    RatingDiff => PointsAfter - PointsBefore;
    }

    public class AltTtGame
    {
        public int           Id            { get; set; }
        public int           CompetitionId { get; set; }
        public AltTtGameSide Me            { get; set; } = new();
        public AltTtGameSide Opponent      { get; set; } = new();
    }

    /// <summary>
    /// In-memory calculation state for one player while replaying the full competition history -
    /// ported from the desktop tool's PRating. Never persisted directly; SaveMonthAsync snapshots
    /// just Points/IsActive per month into the rankings2 table.
    /// </summary>
    internal class AltPRating
    {
        public int Points { get; set; }
        public int GamesPlayed { get; set; }
        public int Competitions { get; set; }
        public string? Gender { get; set; }
        public int GamesVsMale { get; set; }
        public int GamesVsFemale { get; set; }
        public DateTime? LastCompetitionDate { get; set; }
    }

    internal class AltCompetition
    {
        public int Id { get; set; }
        public DateTime StartDate { get; set; }
        public double Coef { get; set; }
    }

    /// <summary>One player's rating move within a single game - kept so the alternative tournament/game
    /// pages can show "what changed for this exact game", which the official ranking computes on the
    /// fly with a simple formula but this engine can't (a whole tournament, and the corrections at its
    /// start, all feed into the result), so it's persisted instead.</summary>
    internal class AltGameDelta
    {
        public int GameId       { get; set; }
        public int PlayerId     { get; set; }
        public int OpponentId   { get; set; }
        public int PointsBefore { get; set; }
        public int PointsAfter  { get; set; }
    }

    /// <summary>One player's rating move across a whole tournament, including any correction applied
    /// at the start (before that tournament's games were scored) and, for a player's first-ever
    /// tournament, the initial rating the engine calculated for them.</summary>
    internal class AltTournamentDelta
    {
        public int  PlayerId          { get; set; }
        public int  CompetitionId     { get; set; }
        public int  PointsBefore      { get; set; }
        public int? Correction        { get; set; }
        public bool IsFirstTournament { get; set; }
        public int? InitialRating     { get; set; }
        public int  PointsAfter       { get; set; }
    }

    internal class AltPlayerResult
    {
        public int PlayerId { get; set; }
        public List<(int OpponentId, int Comps, string? Gender)> WinsVs { get; } = [];
        public List<(int OpponentId, int Comps, string? Gender)> LossVs { get; } = [];
        public int StartRating { get; set; }
        public string? Gender { get; set; }
        public double Coef { get; set; }
        public int AfterInitial { get; set; }
    }
}
