namespace MartinsWeb.Models
{
    // ── Ieteikumi (anonymous suggestions) ──────────────────────────────────

    public class SuggestionEntry
    {
        public int      Id        { get; set; }
        public string   Text      { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    // ── Aptaujas (one admin-set question, free-text answers from anyone) ───

    /// <summary>A survey's question, as shown to someone about to answer it.</summary>
    public class SurveyEntry
    {
        public int    Id       { get; set; }
        public string Question { get; set; } = "";
        public bool   IsActive { get; set; }
    }

    /// <summary>One row in the admin's list of surveys.</summary>
    public class SurveyAdminVm
    {
        public int      Id            { get; set; }
        public string   Question      { get; set; } = "";
        public bool     IsActive      { get; set; }
        public DateTime CreatedAt     { get; set; }
        public int      ResponseCount { get; set; }
    }

    /// <summary>One free-text answer, for the admin's results view.</summary>
    public class SurveyAnswerEntry
    {
        public int      Id        { get; set; }
        public string   Text      { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public class SurveyResults
    {
        public int    Id       { get; set; }
        public string Question { get; set; } = "";
        public bool   IsActive { get; set; }
        public List<SurveyAnswerEntry> Answers { get; set; } = [];
    }
}
