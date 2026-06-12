using System.Net.Http.Json;
using System.Text.Json;
using MartinsWeb.Models;

namespace MartinsWeb.Services;

/// <summary>
/// Wraps the api-sports.io REST API for both hockey and football.
/// Uses per-request HttpRequestMessage so the API key header is never shared
/// across pooled handler instances.
/// </summary>
public class ApiSportsService
{
    private readonly IHttpClientFactory       _http;
    private readonly ILogger<ApiSportsService> _log;

    private const string HockeyBase  = "https://v1.hockey.api-sports.io";
    private const string FootballBase = "https://v3.football.api-sports.io";

    public ApiSportsService(IHttpClientFactory http, ILogger<ApiSportsService> log)
    {
        _http = http;
        _log  = log;
    }

    // ── Connection test ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns account info if the key is valid, null otherwise.
    /// NOTE: /status returns "response" as a plain object {}, not an array [].
    /// </summary>
    public async Task<ApiStatusResponse?> TestKeyAsync(string apiKey, bool isHockey)
    {
        try
        {
            var resp = await SendAsync(apiKey, isHockey, "/status");
            if (!resp.IsSuccessStatusCode) return null;

            var wrapper = await resp.Content.ReadFromJsonAsync<ApiStatusWrapper>();
            return wrapper?.Response;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TestKeyAsync failed");
            return null;
        }
    }

    // ── Leagues ───────────────────────────────────────────────────────────────

    public async Task<List<ApiLeague>> GetLeaguesAsync(string apiKey, bool isHockey)
    {
        try
        {
            var resp = await SendAsync(apiKey, isHockey, "/leagues");
            resp.EnsureSuccessStatusCode();
            var wrapper = await resp.Content.ReadFromJsonAsync<ApiSportsResponse<ApiLeague>>();
            return wrapper?.Response ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetLeaguesAsync failed");
            return new();
        }
    }

    // ── Teams ─────────────────────────────────────────────────────────────────

    public async Task<List<ApiTeamWrapper>> GetTeamsAsync(
        string apiKey, bool isHockey, int leagueId, int season)
    {
        try
        {
            var resp = await SendAsync(apiKey, isHockey,
                $"/teams?league={leagueId}&season={season}");
            resp.EnsureSuccessStatusCode();
            var wrapper = await resp.Content.ReadFromJsonAsync<ApiSportsResponse<ApiTeamWrapper>>();
            return wrapper?.Response ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetTeamsAsync failed");
            return new();
        }
    }

    // ── Games ─────────────────────────────────────────────────────────────────

    public async Task<List<ApiHockeyGame>> GetHockeyGamesAsync(
        string apiKey, int leagueId, int season, DateOnly date)
    {
        try
        {
            var resp = await SendAsync(apiKey, isHockey: true,
                $"/games?league={leagueId}&season={season}&date={date:yyyy-MM-dd}");
            resp.EnsureSuccessStatusCode();
            var wrapper = await resp.Content.ReadFromJsonAsync<ApiSportsResponse<ApiHockeyGame>>();
            return wrapper?.Response ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetHockeyGamesAsync failed for {Date}", date);
            return new();
        }
    }

    public async Task<List<ApiFootballGame>> GetFootballGamesAsync(
        string apiKey, int leagueId, int season, DateOnly date)
    {
        try
        {
            var resp = await SendAsync(apiKey, isHockey: false,
                $"/fixtures?league={leagueId}&season={season}&date={date:yyyy-MM-dd}");
            resp.EnsureSuccessStatusCode();
            var wrapper = await resp.Content.ReadFromJsonAsync<ApiSportsResponse<ApiFootballGame>>();
            return wrapper?.Response ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetFootballGamesAsync failed for {Date}", date);
            return new();
        }
    }

    // ── Core send — header set per request, never on the shared client ────────

    private async Task<HttpResponseMessage> SendAsync(
        string apiKey, bool isHockey, string relativeUrl)
    {
        var baseUrl = isHockey ? HockeyBase : FootballBase;
        var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + relativeUrl);
        request.Headers.Add("x-apisports-key", apiKey);

        var client = _http.CreateClient();
        return await client.SendAsync(request);
    }

    // ── Status helpers ────────────────────────────────────────────────────────

    /// <summary>FT = full time, AOT = after overtime, AP = after penalties.</summary>
    public static bool IsHockeyFinished(string statusShort) =>
        statusShort is "FT" or "AOT" or "AP" or "AET";

    public static bool IsFootballFinished(string statusShort) =>
        statusShort is "FT" or "AET" or "PEN";

    /// <summary>
    /// Both OT (AOT) and shootout/penalties (AP) set IsOvertime = true.
    /// The hockey points calculator treats them identically.
    /// </summary>
    public static bool IsHockeyOvertime(string statusShort, ApiHockeyPeriods periods) =>
        statusShort is "AOT" or "AP"
        || (periods.Overtime.HasValue  && periods.Overtime  > 0)
        || (periods.Penalties.HasValue && periods.Penalties > 0);

    public static bool IsFootballOvertime(ApiFootballScore score) =>
        (score.Extratime?.Home.HasValue ?? false)
        || (score.Penalty?.Home.HasValue ?? false);
}
