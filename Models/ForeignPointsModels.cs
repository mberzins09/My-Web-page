namespace MartinsWeb.Models
{
    /// <summary>One points range with its calibration coefficient (PlayerDB.Points ≈ rankings1 points × Coefficient).</summary>
    public class CalibrationBin
    {
        public double MinX        { get; set; }   // lowest rankings1 points in the range
        public double MaxX        { get; set; }   // highest rankings1 points in the range
        public double CenterX     { get; set; }   // median rankings1 points (coefficients are interpolated between centers)
        public double Coefficient { get; set; }
        public int    Count       { get; set; }   // players used to derive the coefficient
    }

    public class ForeignPointsRow
    {
        public int    PlayerId     { get; set; }
        public string Name         { get; set; } = "";
        public double SourcePoints { get; set; }   // rankings1 points
        public int    OldPoints    { get; set; }   // PlayerDB.Points before
        public int    NewPoints    { get; set; }   // calculated Latvian-scale points
    }

    public class ForeignPointsResult
    {
        public string Month   { get; set; } = "";
        public bool   Applied { get; set; }        // false = preview only

        public List<CalibrationBin>  Bins { get; set; } = [];
        public int    CalibrationPlayers { get; set; }
        public double MeanAbsError       { get; set; }
        public double MedianAbsError     { get; set; }

        public int ForeignsUpdated         { get; set; }   // (would be) updated
        public int ForeignsWithoutRanking  { get; set; }   // gender = null, but no usable rankings1 row
        public int RankingRowsWithoutPlayer { get; set; }  // rankings1 player_id not found in PlayerDB

        public List<ForeignPointsRow> Rows { get; set; } = [];

        /// <summary>Progress lines of the month replay that runs after saving.</summary>
        public List<string> RecalcLog { get; set; } = [];
    }
}
