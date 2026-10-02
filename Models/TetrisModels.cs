namespace MartinsWeb.Models
{
    public class TetrisScoreEntry
    {
        public int      Id         { get; set; }
        public string   PlayerName { get; set; } = "";
        public int      Score      { get; set; }
        public int      Lines      { get; set; }
        public int      DurationSeconds { get; set; }
        public int      Level      { get; set; }
        public DateTime CreatedAt  { get; set; }
    }
}
