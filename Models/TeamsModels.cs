using System.Globalization;
using System.Text.Json.Serialization;

namespace MartinsWeb.Models
{
    public class SeptemberPlayer
    {
        public int Id { get; set; }
        public int ApiPlayerId { get; set; }
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
        public decimal PointsWithBonus { get; set; }
        public bool IsManualEdit { get; set; } = false;
    }

    public class TeamEntry
    {
        public int Id { get; set; }
        public string TeamName { get; set; } = "";
        public bool AlreadyBuilt { get; set; } = false;

        public ICollection<TeamPlayerEntry> Players { get; set; } = [];
    }

    public class TeamPlayerEntry
    {
        public int Id { get; set; }
        public int TeamEntryId { get; set; }
        public int ApiPlayerId { get; set; }
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";

        public TeamEntry Team { get; set; } = null!;
    }

    // ── API response models (internal) ────────────────────────────────────────
    public class RankingPlayer
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("surname")] public string Surname { get; set; } = "";
        [JsonPropertyName("points_with_bonus")]
        public string? PointsWithBonusRaw { get; set; }
        public decimal PointsWithBonus =>
            decimal.TryParse(PointsWithBonusRaw, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    public class RankingListResponse
    {
        [JsonPropertyName("players")] public List<RankingPlayer> Players { get; set; } = new();
    }

    public class ApiParticipant
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("surname")] public string Surname { get; set; } = "";
        [JsonPropertyName("team_name")] public string? TeamName { get; set; }
        [JsonPropertyName("player_id")] public int PlayerId { get; set; }
    }

    public class CompetitionEventResultsResponse
    {
        [JsonPropertyName("participants")]
        public List<ApiParticipant> Participants { get; set; } = new();
    }

    // ── View model returned to the page ───────────────────────────────────────
    public class TeamViewModel
    {
        public int Id { get; set; }
        public string TeamName { get; set; } = "";
        public decimal Points { get; set; }
        public List<PlayerViewModel> Players { get; set; } = [];
    }

    public class PlayerViewModel
    {
        public int ApiPlayerId { get; set; }
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
        public decimal PointsWithBonus { get; set; }
        public bool IsManualEdit { get; set; }
        public bool IsTop3 { get; set; }  // highlights which 3 count
    }
}
