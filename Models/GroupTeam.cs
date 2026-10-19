namespace MartinsWeb.Models
{
    public class GroupTeam
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public string TeamName { get; set; } = "";

        /// <summary>Country.Id this row was picked from, when picked via the dropdown - null for a
        /// manually-typed team or one created before this was tracked.</summary>
        public int? CountryId { get; set; }

        public TournamentGroup Group { get; set; } = null!;
    }
}
