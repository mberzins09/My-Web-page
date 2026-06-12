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

        public ICollection<TournamentGroup> Groups { get; set; } = new List<TournamentGroup>();
        public ICollection<Game> Games { get; set; } = new List<Game>();
    }
}
