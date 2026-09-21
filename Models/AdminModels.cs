namespace MartinsWeb.Models
{
    public class AdminTournamentVm
    {
        public int    Id        { get; set; }
        public string Name      { get; set; } = "";
        public string Date      { get; set; } = "";   // normalised yyyy-MM-dd
        public double Coef      { get; set; }
        public int    GameCount { get; set; }
        public string EventType { get; set; } = "";
        public string Places    { get; set; } = "";
    }

    public class CleanDatabaseResult
    {
        public string? BackupFile          { get; set; }
        public int     DuplicateGroups     { get; set; }
        public int     DuplicatesRemoved   { get; set; }
        public int     GamesRepointed      { get; set; }   // game slots moved from a removed duplicate to the keeper
        public int     GamesResynced       { get; set; }   // game slots re-pointed to PlayerDB.Id by key name
        public bool    PlayersTableDropped { get; set; }
        public bool    Vacuumed            { get; set; }
    }

    public class AdminGameVm
    {
        public int    Id     { get; set; }
        public int    P1Id   { get; set; }
        public int    P2Id   { get; set; }
        public string P1Name { get; set; } = "";
        public string P2Name { get; set; } = "";
        public int    S1     { get; set; }
        public int    S2     { get; set; }
    }
}
