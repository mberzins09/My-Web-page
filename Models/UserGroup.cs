namespace MartinsWeb.Models
{
    /// <summary>
    /// A named competition group of users (e.g. "Family", "Coworkers").
    /// Scoped to one tournament. Admin-only — users cannot see which groups they are in.
    /// </summary>
    public class UserGroup
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int TournamentId { get; set; }
        public Tournament Tournament { get; set; } = null!;
        public ICollection<UserGroupMember> Members { get; set; } = new List<UserGroupMember>();
    }
}
