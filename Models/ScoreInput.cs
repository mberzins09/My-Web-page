namespace MartinsWeb.Models
{
    public class ScoreInput
    {
        public int  Home  { get; set; }
        public int  Away  { get; set; }
        public bool IsOvertime { get; set; }

        // Actual full-time score (used in Scores.razor by admin)
        public int? HomeFullTime { get; set; }
        public int? AwayFullTime { get; set; }

        // Predicted full-time score (used in MyPredictions.razor by user)
        public int? PredHomeFullTime { get; set; }
        public int? PredAwayFullTime { get; set; }
    }
}
