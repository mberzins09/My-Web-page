using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services;

/// <summary>
/// Runs every 30 minutes. For every tournament with an API-Sports config, finds
/// local games that started more than 3 hours ago and still have no score,
/// then fetches the result from api-sports.io and saves it.
/// </summary>
public class ApiScoreSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApiScoreSyncService>  _log;

    private static readonly TimeSpan Interval   = TimeSpan.FromMinutes(30);
    private static readonly TimeZoneInfo Latvia  =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Riga");

    public ApiScoreSyncService(IServiceScopeFactory scopeFactory,
                               ILogger<ApiScoreSyncService> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait 30 s on startup so the app is fully ready before first run
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await RunSyncAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "ApiScoreSyncService unhandled error"); }

            await Task.Delay(Interval, ct);
        }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        using var scope      = _scopeFactory.CreateScope();
        var db               = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var apiSvc           = scope.ServiceProvider.GetRequiredService<ApiSportsService>();
        var gameSvc          = scope.ServiceProvider.GetRequiredService<GameService>();

        DateTime latviaTime  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Latvia);
        DateTime cutoff      = latviaTime.AddHours(-3);   // only games started > 3 h ago

        // Load all configs that have a league selected AND are enabled
        var configs = await db.ApiSportsConfigs
            .Include(c => c.Tournament)
            .Where(c => c.LeagueId != null && c.IsEnabled)
            .ToListAsync(ct);

        if (!configs.Any()) return;

        // Load all mappings in one query (keyed by TournamentId)
        var allMappings = await db.ApiTeamMappings.ToListAsync(ct);

        foreach (var config in configs)
        {
            if (ct.IsCancellationRequested) break;

            bool isHockey = config.Tournament?.PointsCalculationType == "Hockey";

            // Find local games for this tournament that need a score
            var pendingGames = await db.Games
                .Where(g => g.TournamentId == config.TournamentId
                         && g.HomeScore == null
                         && g.GameDate   <= cutoff)
                .ToListAsync(ct);

            if (!pendingGames.Any()) continue;

            var mappings = allMappings
                .Where(m => m.TournamentId == config.TournamentId)
                .ToList();

            _log.LogInformation(
                "ApiSync: tournament {T} — {N} pending game(s)",
                config.Tournament!.Name, pendingGames.Count);

            // Group by date to minimise API calls (one call per unique date)
            var byDate = pendingGames.GroupBy(g => DateOnly.FromDateTime(g.GameDate));

            foreach (var dateGroup in byDate)
            {
                if (ct.IsCancellationRequested) break;

                DateOnly gameDate = dateGroup.Key;

                if (isHockey)
                    await SyncHockeyDate(db, apiSvc, gameSvc, config, mappings,
                                         dateGroup.ToList(), gameDate, ct);
                else
                    await SyncFootballDate(db, apiSvc, gameSvc, config, mappings,
                                           dateGroup.ToList(), gameDate, ct);

                // Respect rate limit: no more than ~1 call/s on the free plan
                await Task.Delay(1100, ct);
            }
        }
    }

    // ── Hockey ────────────────────────────────────────────────────────────────

    private async Task SyncHockeyDate(
        AppDbContext db, ApiSportsService apiSvc, GameService gameSvc,
        ApiSportsConfig config, List<ApiTeamMapping> mappings,
        List<Game> localGames, DateOnly date, CancellationToken ct)
    {
        var apiGames = await apiSvc.GetHockeyGamesAsync(
            config.ApiKey, config.LeagueId!.Value, config.SeasonYear, date);

        foreach (var local in localGames)
        {
            var homeMap = mappings.FirstOrDefault(m => m.LocalTeamName == local.HomeTeam);
            var awayMap = mappings.FirstOrDefault(m => m.LocalTeamName == local.AwayTeam);

            if (homeMap == null || awayMap == null)
            {
                await WriteLog(db, config.TournamentId, local.Id, false,
                    $"No API mapping for '{local.HomeTeam}' or '{local.AwayTeam}'");
                continue;
            }

            // Match by team IDs + same date (handles two games between same teams)
            var match = apiGames.FirstOrDefault(g =>
                g.Teams.Home.Id == homeMap.ApiTeamId &&
                g.Teams.Away.Id == awayMap.ApiTeamId &&
                ApiSportsService.IsHockeyFinished(g.Status.Short));

            if (match == null)
            {
                await WriteLog(db, config.TournamentId, local.Id, false,
                    $"No finished API game found for {local.HomeTeam} vs {local.AwayTeam} on {date}");
                continue;
            }

            bool ot = ApiSportsService.IsHockeyOvertime(match.Status.Short, match.Periods);
            await gameSvc.UpdateScoreAsync(local.Id,
                match.Scores.Home ?? 0, match.Scores.Away ?? 0, ot);

            await WriteLog(db, config.TournamentId, local.Id, true,
                $"Saved {match.Scores.Home}:{match.Scores.Away}{(ot ? " OT" : "")} (API game {match.Id})");

            _log.LogInformation(
                "ApiSync: saved {H}:{A}{OT} for game {G}",
                match.Scores.Home, match.Scores.Away, ot ? " OT" : "", local.Id);
        }
    }

    // ── Football ──────────────────────────────────────────────────────────────

    private async Task SyncFootballDate(
        AppDbContext db, ApiSportsService apiSvc, GameService gameSvc,
        ApiSportsConfig config, List<ApiTeamMapping> mappings,
        List<Game> localGames, DateOnly date, CancellationToken ct)
    {
        var apiGames = await apiSvc.GetFootballGamesAsync(
            config.ApiKey, config.LeagueId!.Value, config.SeasonYear, date);

        foreach (var local in localGames)
        {
            var homeMap = mappings.FirstOrDefault(m => m.LocalTeamName == local.HomeTeam);
            var awayMap = mappings.FirstOrDefault(m => m.LocalTeamName == local.AwayTeam);

            if (homeMap == null || awayMap == null)
            {
                await WriteLog(db, config.TournamentId, local.Id, false,
                    $"No API mapping for '{local.HomeTeam}' or '{local.AwayTeam}'");
                continue;
            }

            var match = apiGames.FirstOrDefault(g =>
                g.Teams.Home.Id == homeMap.ApiTeamId &&
                g.Teams.Away.Id == awayMap.ApiTeamId &&
                ApiSportsService.IsFootballFinished(g.Fixture.Status.Short));

            if (match == null)
            {
                await WriteLog(db, config.TournamentId, local.Id, false,
                    $"No finished API game found for {local.HomeTeam} vs {local.AwayTeam} on {date}");
                continue;
            }

            bool ot = ApiSportsService.IsFootballOvertime(match.Score);
            await gameSvc.UpdateScoreAsync(local.Id,
                match.Goals.Home ?? 0, match.Goals.Away ?? 0, ot);

            await WriteLog(db, config.TournamentId, local.Id, true,
                $"Saved {match.Goals.Home}:{match.Goals.Away}{(ot ? " OT/Pen" : "")} (API fixture {match.Fixture.Id})");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WriteLog(AppDbContext db, int tournamentId, int gameId,
                                        bool success, string message)
    {
        // Keep only the most recent log entry per game (no unbounded growth)
        var existing = db.ApiSyncLogs.Where(l => l.GameId == gameId);
        db.ApiSyncLogs.RemoveRange(existing);

        db.ApiSyncLogs.Add(new ApiSyncLog
        {
            TournamentId = tournamentId,
            GameId       = gameId,
            RunAt        = DateTime.UtcNow,
            Success      = success,
            Message      = message
        });
        await db.SaveChangesAsync();
    }
}
