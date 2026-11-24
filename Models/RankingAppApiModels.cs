namespace MartinsWeb.Models
{
    // DTOs returned by the public /api/rankingapp/* endpoints used by the RankingApp mobile app.
    // Property names must stay in sync with RankingApp.Core/Models/LgtfApiModels.cs.

    public class RankingAppPlayer
    {
        public int     Id              { get; set; }
        public string  Name            { get; set; } = "";
        public string  Surname         { get; set; } = "";
        public string? BirthDate       { get; set; }
        public string? Gender          { get; set; }
        public int     Place           { get; set; }
        public int     OverallPlace    { get; set; }
        public int     Points          { get; set; }
        public int     PointsWithBonus { get; set; }
        public bool    IsActive        { get; set; }
        public int     NewId           { get; set; }
        public string? KeyName         { get; set; }
    }

    public class RankingAppCompetition
    {
        public int     Id        { get; set; }
        public string  Name      { get; set; } = "";
        public string  StartDate { get; set; } = "";
        public string? Places    { get; set; }
        public double  Coef      { get; set; }
    }

    public class RankingAppGame
    {
        public int    Id              { get; set; }
        public int    CompetitionId   { get; set; }

        public int    Player1Id       { get; set; }
        public int    Player2Id       { get; set; }
        public string Player1Name     { get; set; } = "";
        public string Player1Surname  { get; set; } = "";
        public string Player2Name     { get; set; } = "";
        public string Player2Surname  { get; set; } = "";
        public string Player1KeyName  { get; set; } = "";
        public string Player2KeyName  { get; set; } = "";

        public int    Player1Sets     { get; set; }
        public int    Player2Sets     { get; set; }
        public int    Player1Points          { get; set; }
        public int    Player2Points          { get; set; }
        public int    Player1PointsWithBonus { get; set; }
        public int    Player2PointsWithBonus { get; set; }
        public int    Player1Age      { get; set; }
        public int    Player2Age      { get; set; }
        public int    Player1Place    { get; set; }
        public int    Player2Place    { get; set; }
    }

    public class RankingAppPlayerHistory
    {
        /// <summary>The player's Id in lgtf.sqlite (compare with Player1Id / Player2Id).</summary>
        public int    PlayerId { get; set; }
        public string KeyName  { get; set; } = "";
        public List<RankingAppCompetition> Competitions { get; set; } = [];
        public List<RankingAppGame>        Games        { get; set; } = [];
    }
}
