namespace MartinsWeb.Models
{
    public class GroupTeam
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public string TeamName { get; set; } = "";

        public TournamentGroup Group { get; set; } = null!;
    }
}
