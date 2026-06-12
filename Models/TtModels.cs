using System.Text.Json.Serialization;

namespace MartinsWeb.Models
{
    public class TtH2HGame
    {
        public int    GameId          { get; set; }
        public int    CompetitionId   { get; set; }
        public string CompetitionName { get; set; } = "";
        public string Date            { get; set; } = "";
        public int    PlayerASets     { get; set; }
        public int    PlayerBSets     { get; set; }

        /// <summary>Rating delta for Player A in this game (may be 0 if no points data).</summary>
        public int PlayerARatingDiff  { get; set; }
        /// <summary>Rating delta for Player B in this game (may be 0 if no points data).</summary>
        public int PlayerBRatingDiff  { get; set; }

        public DateTime ParsedDate => DateTime.TryParse(Date, out var d) ? d : DateTime.MinValue;
    }

    // ── Display models ────────────────────────────────────────────────────────

    public class TtPlayer
    {
        public int    Id        { get; set; }
        public string Name      { get; set; } = "";
        public string Surname   { get; set; } = "";
        public string BirthDate { get; set; } = "";
        public string KeyName   { get; set; } = "";
        public string Gender    { get; set; } = "";
        public string FullName  => $"{Name} {Surname}";
    }

    public class TtCompetition
    {
        public int    Id         { get; set; }
        public string Name       { get; set; } = "";
        public string StartDate  { get; set; } = "";
        public string Places     { get; set; } = "";
        public double Coef       { get; set; }
        public int    GamesCount { get; set; }
        public int    Wins       { get; set; }
        public int    Losses     { get; set; }

        /// <summary>Sum of rating deltas across all games in this tournament for the queried player.</summary>
        public int RatingDiffTotal { get; set; }

        public DateTime Date => DateTime.TryParse(StartDate, out var d) ? d : DateTime.MinValue;
        public int Year => Date.Year;
    }

    public class TtGameSide
    {
        public int    PlayerId        { get; set; }
        public string Name            { get; set; } = "";
        public string Surname         { get; set; } = "";
        public int    Sets            { get; set; }
        public int    Points          { get; set; }
        public int    PointsWithBonus { get; set; }
        public int    Age             { get; set; }
        public int    Place           { get; set; }
        public bool   IsWinner        { get; set; }
        public string FullName        => $"{Name} {Surname}";
    }

    public class TtGame
    {
        public int        Id            { get; set; }
        public int        CompetitionId { get; set; }
        public double     Coef          { get; set; }   // competition coef, needed for rating calc
        public TtGameSide Me            { get; set; } = new();
        public TtGameSide Opponent      { get; set; } = new();

        /// <summary>Rating delta for Me in this game. Calculated from RatingCalculator.</summary>
        public int MyRatingDiff       => Me.Points > 0 && Opponent.Points > 0
            ? RatingCalculator.Calculate(Me.Points, Opponent.Points, Me.IsWinner, Coef)
            : 0;

        /// <summary>Rating delta for Opponent in this game.</summary>
        public int OpponentRatingDiff => Me.Points > 0 && Opponent.Points > 0
            ? RatingCalculator.Calculate(Opponent.Points, Me.Points, Opponent.IsWinner, Coef)
            : 0;
    }

    // ── Tournament API response models ────────────────────────────────────────

    public class TtCompetitionEventsResponse
    {
        public List<TtCompetitionEventListItem>? competition_events { get; set; }
    }

    public class TtCompetitionEventListItem
    {
        public int    id                         { get; set; }
        public string name                       { get; set; } = "";
        public string type                       { get; set; } = "singles";
        public string start_date                 { get; set; } = "";
        public string effective_date             { get; set; } = "";
        public string end_date                   { get; set; } = "";
        public string ranking_coef               { get; set; } = "0";
        public bool   is_season_ranking_instance { get; set; }
        public TtCompetitionInfo? competition    { get; set; }
    }

    public class TtEventResultResponse
    {
        public TtCompetitionEvent? competition_event { get; set; }
        public List<TtNet>?        nets              { get; set; }
    }

    public class TtCompetitionEvent
    {
        public int    id           { get; set; }
        public string name         { get; set; } = "";
        public string start_date   { get; set; } = "";
        public string end_date     { get; set; } = "";
        public string ranking_coef { get; set; } = "0";
        public TtCompetitionInfo? competition { get; set; }
    }

    public class TtCompetitionInfo
    {
        public string name   { get; set; } = "";
        public string places { get; set; } = "";
    }

    public class TtNet
    {
        public List<TtGroup>?           groups          { get; set; }
        public List<TtEliminationTree>? elimination_trees { get; set; }
    }

    public class TtGroup
    {
        public List<TtGroupGame>? games { get; set; }
    }

    public class TtGroupGame
    {
        public int            id            { get; set; }
        public string?        game_type     { get; set; }
        public TtPlayerShort? player1       { get; set; }
        public TtPlayerShort? player2       { get; set; }
        public string?        player1_score { get; set; }
        public string?        player2_score { get; set; }
        public List<TtGroupGame>? games     { get; set; }
    }

    public class TtEliminationTree
    {
        public List<TtRound>? rounds { get; set; }
    }

    public class TtRound
    {
        public List<TtGroupGame>? games { get; set; }
    }

    public class TtPlayerShort
    {
        public int    id      { get; set; }
        public string name    { get; set; } = "";
        public string surname { get; set; } = "";
    }

    public class TtRankingOldResponse
    {
        [JsonPropertyName("players")]
        public List<TtRankingOldPlayer>? Players { get; set; }
    }

    public class TtRankingOldPlayer
    {
        [JsonPropertyName("name")]              public string? Name            { get; set; }
        [JsonPropertyName("surname")]           public string? Surname         { get; set; }
        [JsonPropertyName("vieta")]             public int     Place           { get; set; }
        [JsonPropertyName("points")]            public int     Points          { get; set; }
        [JsonPropertyName("points_with_bonus")] public int     PointsWithBonus { get; set; }
        [JsonPropertyName("dzimsanas_dat")]     public string? BirthDate       { get; set; }
    }

    public class TtRankingNewResponse
    {
        [JsonPropertyName("players")]
        public List<TtRankingNewPlayer>? Players { get; set; }
    }

    public class TtRankingNewPlayer
    {
        [JsonPropertyName("name")]              public string? Name            { get; set; }
        [JsonPropertyName("surname")]           public string? Surname         { get; set; }
        [JsonPropertyName("rank")]              public int     Rank            { get; set; }
        [JsonPropertyName("points")]            public string? Points          { get; set; }
        [JsonPropertyName("points_with_bonus")] public string? PointsWithBonus { get; set; }
        [JsonPropertyName("birth_date")]        public string? BirthDate       { get; set; }
    }

    public class TtRankedPlayer
    {
        public string KeyName         { get; set; } = "";
        public int    Place           { get; set; }
        public int    Points          { get; set; }
        public int    PointsWithBonus { get; set; }
        public string BirthDate       { get; set; } = "";
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
    }
}
