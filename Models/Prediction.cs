using System.Text.RegularExpressions;

namespace MartinsWeb.Models
{
    public class Prediction
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; } = null!;
        public int GameId { get; set; }
        public Game Game { get; set; } = null!;
        public int PredictedHomeScore { get; set; }
        public int PredictedAwayScore { get; set; }
        public bool PredictedIsOvertime { get; set; }   // Hockey: did user predict overtime?
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
