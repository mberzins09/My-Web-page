namespace MartinsWeb.Models
{
    /// <summary>
    /// Join table: which users belong to which UserGroup.
    /// One user can be in multiple groups.
    /// </summary>
    public class UserGroupMember
    {
        public int Id { get; set; }
        public int UserGroupId { get; set; }
        public UserGroup UserGroup { get; set; } = null!;
        public int UserId { get; set; }
        public User User { get; set; } = null!;
    }
}
