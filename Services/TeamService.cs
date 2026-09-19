using MartinsWeb.Data;
using MartinsWeb.Models;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    public class TeamService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _http;
        private const string ApiKey = "org_trJaxebjAq9bQjdkPb1PJONCO1Im8befEFv7w8Jr";
        private const string CompetitionEventId = "1645";
        private const string RankingUrl =
            "https://turniri.lgtf.lv/api/v1/ranking-list?ranking_id=2&gender=male&year=2026&month=9";
        private const string CompetitionUrl =
            "https://turniri.lgtf.lv/api/v1/competition-event-results?competition_event_id=" + CompetitionEventId;

        public TeamService(AppDbContext db, IHttpClientFactory http)
        {
            _db = db;
            _http = http;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Get all teams as view models, ordered by points descending.</summary>
        public async Task<List<TeamViewModel>> GetTeamsAsync()
        {
            var teams = await _db.TeamEntries.Include(t => t.Players).ToListAsync();
            var players = await _db.SeptemberPlayers.ToListAsync();
            var byId = players.ToDictionary(p => p.ApiPlayerId);
            return BuildViewModels(teams, byId);
        }

        /// <summary>
        /// Fetch from API and insert new teams/players that weren't there before.
        /// Already-built teams are skipped (AlreadyBuilt flag).
        /// Returns count of new teams added.
        /// </summary>
        public async Task<int> RefetchTeamsAsync()
        {
            var (rankingPlayers, participants) = await FetchFromApiAsync();

            // Upsert SeptemberPlayers — never overwrite manual edits
            foreach (var rp in rankingPlayers)
            {
                var existing = await _db.SeptemberPlayers
                    .FirstOrDefaultAsync(p => p.ApiPlayerId == rp.Id);
                if (existing == null)
                {
                    _db.SeptemberPlayers.Add(new SeptemberPlayer
                    {
                        ApiPlayerId = rp.Id,
                        Name = rp.Name,
                        Surname = rp.Surname,
                        PointsWithBonus = rp.PointsWithBonus
                    });
                }
                else if (!existing.IsManualEdit)
                {
                    existing.PointsWithBonus = rp.PointsWithBonus;
                }
            }
            await _db.SaveChangesAsync();

            // Add new teams
            var existingTeamNames = await _db.TeamEntries
                .Select(t => t.TeamName).ToListAsync();

            int added = 0;
            foreach (var group in participants
                .Where(p => !string.IsNullOrWhiteSpace(p.TeamName))
                .GroupBy(p => p.TeamName!))
            {
                if (existingTeamNames.Contains(group.Key)) continue;

                var team = new TeamEntry
                {
                    TeamName = group.Key,
                    AlreadyBuilt = true,
                    Players = group.Select(p => new TeamPlayerEntry
                    {
                        ApiPlayerId = p.PlayerId,
                        Name = p.Name,
                        Surname = p.Surname
                    }).ToList()
                };
                _db.TeamEntries.Add(team);
                added++;
            }
            await _db.SaveChangesAsync();
            return added;
        }

        /// <summary>
        /// Full initial fetch — used when DB is empty.
        /// </summary>
        public async Task InitialFetchAsync()
        {
            if (await _db.TeamEntries.AnyAsync()) return;
            await RefetchTeamsAsync();
        }

        /// <summary>
        /// Edit a player's points. If they're not in SeptemberPlayers yet, insert them.
        /// Then recalculate team totals (in memory — stored in view models).
        /// </summary>
        public async Task UpdatePlayerPointsAsync(int apiPlayerId, string name, string surname, decimal newPoints)
        {
            var existing = await _db.SeptemberPlayers
                .FirstOrDefaultAsync(p => p.ApiPlayerId == apiPlayerId);

            if (existing != null)
            {
                existing.PointsWithBonus = newPoints;
                existing.IsManualEdit = true;
            }
            else
            {
                _db.SeptemberPlayers.Add(new SeptemberPlayer
                {
                    ApiPlayerId = apiPlayerId,
                    Name = name,
                    Surname = surname,
                    PointsWithBonus = newPoints,
                    IsManualEdit = true
                });
            }
            await _db.SaveChangesAsync();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<(List<RankingPlayer>, List<ApiParticipant>)> FetchFromApiAsync()
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
            client.DefaultRequestHeaders.Accept
                  .Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var ranking = await client.GetFromJsonAsync<RankingListResponse>(RankingUrl);
            var comp = await client.GetFromJsonAsync<CompetitionEventResultsResponse>(CompetitionUrl);

            return (ranking?.Players ?? new(), comp?.Participants ?? new());
        }

        private static List<TeamViewModel> BuildViewModels(
            List<TeamEntry> teams,
            Dictionary<int, SeptemberPlayer> byId)
        {
            return teams
                .Select(team =>
                {
                    var players = team.Players
                        .Select(p =>
                        {
                            byId.TryGetValue(p.ApiPlayerId, out var sp);
                            return new PlayerViewModel
                            {
                                ApiPlayerId = p.ApiPlayerId,
                                Name = p.Name,
                                Surname = p.Surname,
                                PointsWithBonus = sp?.PointsWithBonus ?? 0,
                                IsManualEdit = sp?.IsManualEdit ?? false
                            };
                        })
                        .OrderByDescending(p => p.PointsWithBonus)
                        .ToList();

                    // Mark top 3 that count toward team points
                    for (int i = 0; i < Math.Min(3, players.Count); i++)
                        players[i].IsTop3 = true;

                    decimal teamPoints = players
                        .Where(p => p.IsTop3)
                        .Sum(p => p.PointsWithBonus);

                    return new TeamViewModel
                    {
                        Id = team.Id,
                        TeamName = team.TeamName,
                        Points = teamPoints,
                        Players = players
                    };
                })
                .OrderByDescending(t => t.Points)
                .ToList();
        }
    }
}
