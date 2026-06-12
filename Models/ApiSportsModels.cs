using System.Text.Json.Serialization;

namespace MartinsWeb.Models;

// ── DB entities ───────────────────────────────────────────────────────────────

/// <summary>Stores the API-Sports configuration for one tournament.</summary>
public class ApiSportsConfig
{
    public int    Id             { get; set; }
    public int    TournamentId   { get; set; }
    public string ApiKey         { get; set; } = "";
    public int?   LeagueId       { get; set; }
    public string LeagueName     { get; set; } = "";
    public int    SeasonYear     { get; set; } = DateTime.UtcNow.Year;
    /// <summary>When false, the background sync service skips this tournament entirely.</summary>
    public bool   IsEnabled      { get; set; } = true;

    public Tournament? Tournament { get; set; }
}

/// <summary>Maps a local team name (as typed in the app) to an API-Sports team ID.</summary>
public class ApiTeamMapping
{
    public int    Id            { get; set; }
    public int    TournamentId  { get; set; }
    public string LocalTeamName { get; set; } = "";  // HomeTeam / AwayTeam value in Game
    public int    ApiTeamId     { get; set; }
    public string ApiTeamName   { get; set; } = "";  // display only

    public Tournament? Tournament { get; set; }
}

/// <summary>Records each auto-sync attempt per game so we can surface status in admin UI.</summary>
public class ApiSyncLog
{
    public int      Id           { get; set; }
    public int      TournamentId { get; set; }
    public int      GameId       { get; set; }
    public DateTime RunAt        { get; set; } = DateTime.UtcNow;
    public bool     Success      { get; set; }
    public string   Message      { get; set; } = "";
}

// ── API-Sports response DTOs ──────────────────────────────────────────────────
// These mirror the JSON returned by api-sports.io for hockey and football.

public class ApiSportsResponse<T>
{
    [JsonPropertyName("results")] public int      Results  { get; set; }
    [JsonPropertyName("response")] public List<T> Response { get; set; } = new();
    [JsonPropertyName("errors")]   public object? Errors   { get; set; }
}

// -- Leagues --
public class ApiLeague
{
    [JsonPropertyName("league")] public ApiLeagueInfo League  { get; set; } = new();
    [JsonPropertyName("country")] public ApiCountry  Country  { get; set; } = new();
    [JsonPropertyName("seasons")] public List<ApiSeason> Seasons { get; set; } = new();
}

public class ApiLeagueInfo
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
}

public class ApiCountry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
}

public class ApiSeason
{
    [JsonPropertyName("year")]    public int  Year    { get; set; }
    [JsonPropertyName("current")] public bool Current { get; set; }
}

// -- Teams --
public class ApiTeamWrapper
{
    [JsonPropertyName("team")] public ApiTeamInfo Team { get; set; } = new();
}

public class ApiTeamInfo
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
}

// -- Games (hockey) --
public class ApiHockeyGame
{
    [JsonPropertyName("id")]     public int              Id     { get; set; }
    [JsonPropertyName("date")]   public string           Date   { get; set; } = "";
    [JsonPropertyName("teams")]  public ApiGameTeams     Teams  { get; set; } = new();
    [JsonPropertyName("scores")] public ApiHockeyScores  Scores { get; set; } = new();
    [JsonPropertyName("status")] public ApiGameStatus    Status { get; set; } = new();
    [JsonPropertyName("periods")] public ApiHockeyPeriods Periods { get; set; } = new();
}

public class ApiGameTeams
{
    [JsonPropertyName("home")] public ApiTeamInfo Home { get; set; } = new();
    [JsonPropertyName("away")] public ApiTeamInfo Away { get; set; } = new();
}

public class ApiHockeyScores
{
    [JsonPropertyName("home")] public int? Home { get; set; }
    [JsonPropertyName("away")] public int? Away { get; set; }
}

public class ApiHockeyPeriods
{
    [JsonPropertyName("overtime")] public int? Overtime { get; set; }
    [JsonPropertyName("penalties")] public int? Penalties { get; set; }
}

public class ApiGameStatus
{
    [JsonPropertyName("long")]  public string Long  { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
}

// -- Games (football) --
public class ApiFootballGame
{
    [JsonPropertyName("fixture")] public ApiFixture       Fixture { get; set; } = new();
    [JsonPropertyName("teams")]   public ApiGameTeams     Teams   { get; set; } = new();
    [JsonPropertyName("goals")]   public ApiFootballGoals Goals   { get; set; } = new();
    [JsonPropertyName("score")]   public ApiFootballScore Score   { get; set; } = new();
}

public class ApiFixture
{
    [JsonPropertyName("id")]     public int    Id     { get; set; }
    [JsonPropertyName("date")]   public string Date   { get; set; } = "";
    [JsonPropertyName("status")] public ApiGameStatus Status { get; set; } = new();
}

public class ApiFootballGoals
{
    [JsonPropertyName("home")] public int? Home { get; set; }
    [JsonPropertyName("away")] public int? Away { get; set; }
}

public class ApiFootballScore
{
    [JsonPropertyName("extratime")] public ApiFootballGoals? Extratime { get; set; }
    [JsonPropertyName("penalty")]   public ApiFootballGoals? Penalty   { get; set; }
}

// -- Status check --
// /status returns "response" as a plain object {}, NOT an array [] like other endpoints
public class ApiStatusWrapper
{
    [JsonPropertyName("response")] public ApiStatusResponse? Response { get; set; }
}

public class ApiStatusResponse
{
    [JsonPropertyName("account")]      public ApiAccount?      Account      { get; set; }
    [JsonPropertyName("subscription")] public ApiSubscription? Subscription { get; set; }
    [JsonPropertyName("requests")]     public ApiRequests?     Requests     { get; set; }
}

public class ApiAccount
{
    [JsonPropertyName("firstname")] public string Firstname { get; set; } = "";
    [JsonPropertyName("lastname")]  public string Lastname  { get; set; } = "";
}

public class ApiSubscription
{
    [JsonPropertyName("plan")]   public string Plan   { get; set; } = "";
    [JsonPropertyName("active")] public bool   Active { get; set; }
}

public class ApiRequests
{
    [JsonPropertyName("current")]    public int Current   { get; set; }
    [JsonPropertyName("limit_day")]  public int LimitDay  { get; set; }
}
