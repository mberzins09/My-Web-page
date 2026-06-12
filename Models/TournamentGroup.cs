namespace MartinsWeb.Models
{
    public class TournamentGroup
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";      // "Group A" — also becomes Game.Stage
        public string Event { get; set; } = "";     // kept for display / legacy reference
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Which prediction game this group belongs to
        public int? TournamentId { get; set; }
        public Tournament? Tournament { get; set; }

        public ICollection<GroupTeam> Teams { get; set; } = [];
        public ICollection<Game> Games { get; set; } = [];
    }
}
