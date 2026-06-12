namespace MartinsWeb.Models
{
    /// <summary>
    /// One completed prediction tournament, either auto-filled from a hosted
    /// tournament or manually entered by admin for an external event.
    /// </summary>
    public class PredictionsHistory
    {
        public int     Id             { get; set; }

        /// <summary>Null for manually-entered external tournaments.</summary>
        public int?    TournamentId   { get; set; }
        public Tournament? Tournament { get; set; }

        public string  TournamentName { get; set; } = "";
        public DateTime CompletedAt   { get; set; } = DateTime.UtcNow;

        public ICollection<PredictionsHistoryEntry> Entries { get; set; }
            = new List<PredictionsHistoryEntry>();
    }

    /// <summary>One participant row inside a PredictionsHistory record.</summary>
    public class PredictionsHistoryEntry
    {
        public int    Id                   { get; set; }
        public int    PredictionsHistoryId { get; set; }
        public PredictionsHistory History  { get; set; } = null!;

        /// <summary>Null for free-text participants (non-registered users).</summary>
        public int?   UserId               { get; set; }
        public User?  User                 { get; set; }

        /// <summary>Free-text name for participants who aren't registered users.</summary>
        public string PlayerName           { get; set; } = "";

        public int    Points               { get; set; }
    }
}
