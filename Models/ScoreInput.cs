namespace MartinsWeb.Models
{
    public class ScoreInput
    {
        public int Home { get; set; }
        public int Away { get; set; }
        public bool IsOvertime { get; set; }
        public int? HomeFullTime { get; set; }
        public int? AwayFullTime { get; set; }
        public int? PredHomeFullTime { get; set; }   // NEW: predicted FT by user
        public int? PredAwayFullTime { get; set; }   // NEW: predicted FT by user
    }
}
