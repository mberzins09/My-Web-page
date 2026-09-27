using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    public class GameService(AppDbContext db, PlayoffService playoffs)
    {
        private readonly AppDbContext _db = db;
        private readonly PlayoffService _playoffs = playoffs;

        /// <summary>
        /// Matches an existing tournament's plain-text team names against the countries table by
        /// exact name (case-insensitive) and, where they match, rewrites the team to
        /// "🇱🇻 Latvia(12)" and links it to that country - the same result picking it from the
        /// dropdown would give. Meant for old tournaments that predate the countries feature, so
        /// their archive pages look consistent with newer ones. Only touches sides that aren't
        /// already linked to a country (HomeCountryIds/AwayCountryIds empty), so running it twice
        /// is harmless. Games with 2+ candidates (a playoff placeholder slot) are skipped, since
        /// their text is flags-only and not a single team name to match against.
        /// </summary>
        public async Task<TeamMatchResult> MatchTeamsToCountriesAsync(int tournamentId)
        {
            var result = new TeamMatchResult();

            var tournament = await _db.Tournaments.FindAsync(tournamentId);
            if (tournament == null) return result;

            var countries = await _db.Countries.Include(c => c.Rankings).ToListAsync();
            var byId   = countries.ToDictionary(c => c.Id);
            var byName = new Dictionary<string, Country>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in countries)
                byName.TryAdd(c.Name.Trim(), c);   // first one wins if two countries somehow share a name

            string sport = CountryService.ResolveSport(tournament);
            var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A team's stored text can already be "🇱🇻 Latvia" or "🇱🇻 Latvia(12)" - e.g. every game a
            // group creates gets its text from the dropdown at creation time, flag and (rank) included.
            // Recovering the plain name means stripping a leading flag that matches one of our own
            // countries, then a trailing "(12)" rank, before trying the exact-name lookup.
            var flagPrefixes = countries.Where(c => !string.IsNullOrEmpty(c.FlagEmoji))
                                         .Select(c => c.FlagEmoji).Distinct().OrderByDescending(f => f.Length).ToList();
            string StripFormatting(string text)
            {
                string s = text.Trim();
                var flag = flagPrefixes.FirstOrDefault(f => s.StartsWith(f, StringComparison.Ordinal));
                if (flag != null) s = s[flag.Length..].Trim();

                int paren = s.LastIndexOf('(');
                if (paren > 0 && s.EndsWith(')'))
                    s = s[..paren].Trim();

                return s;
            }

            bool TryMatchSide(Game g, bool isHome)
            {
                string? existingIds = isHome ? g.HomeCountryIds : g.AwayCountryIds;

                // Already linked to one country - just refresh its displayed rank in case a ranking
                // was added or changed since this text was last written (this is what makes a newly
                // entered ranking actually show up on an old game without re-picking the team).
                if (!string.IsNullOrEmpty(existingIds))
                {
                    var ids = PlayoffService.ParseIds(existingIds);
                    if (ids.Count != 1 || !byId.TryGetValue(ids[0], out var linked)) return false;

                    string refreshed = CountryService.Format(linked, sport, tournament.Year);
                    string current = isHome ? g.HomeTeam : g.AwayTeam;
                    if (refreshed == current) return false;

                    if (isHome) g.HomeTeam = refreshed; else g.AwayTeam = refreshed;
                    return true;
                }

                string rawName = (isHome ? g.HomeTeam : g.AwayTeam)?.Trim() ?? "";
                if (rawName.Length == 0) return false;

                // Try the text as-is first (a genuinely plain, never-formatted name), then with any
                // flag/rank formatting stripped (a group-created game whose text already has both).
                if (!byName.TryGetValue(rawName, out var country) &&
                    !byName.TryGetValue(StripFormatting(rawName), out country))
                {
                    unmatched.Add(rawName);
                    return false;
                }

                string text = CountryService.Format(country, sport, tournament.Year);
                if (isHome) { g.HomeTeam = text; g.HomeCountryIds = country.Id.ToString(); }
                else        { g.AwayTeam = text; g.AwayCountryIds = country.Id.ToString(); }
                return true;
            }

            var games = await _db.Games.Where(g => g.TournamentId == tournamentId).ToListAsync();
            foreach (var g in games)
            {
                bool homeMatched = TryMatchSide(g, isHome: true);
                bool awayMatched = TryMatchSide(g, isHome: false);
                if (homeMatched) result.SidesMatched++;
                if (awayMatched) result.SidesMatched++;
                if (homeMatched || awayMatched) result.GamesUpdated++;
            }

            var groupTeams = await _db.TournamentGroups
                .Where(gr => gr.TournamentId == tournamentId)
                .Include(gr => gr.Teams)
                .SelectMany(gr => gr.Teams)
                .ToListAsync();

            foreach (var gt in groupTeams)
            {
                // A row created via the dropdown already knows its country - just refresh the rank.
                Country? country = gt.CountryId.HasValue && byId.TryGetValue(gt.CountryId.Value, out var linked)
                    ? linked
                    : byName.TryGetValue(StripFormatting(gt.TeamName), out var found) ? found : null;

                if (country == null) continue;

                string refreshed = CountryService.Format(country, sport, tournament.Year);
                if (refreshed != gt.TeamName || gt.CountryId != country.Id)
                {
                    gt.TeamName  = refreshed;
                    gt.CountryId = country.Id;
                    result.GroupTeamsMatched++;
                }
            }

            result.UnmatchedNames = unmatched.OrderBy(n => n).ToList();

            if (result.GamesUpdated > 0 || result.GroupTeamsMatched > 0)
                await _db.SaveChangesAsync();

            return result;
        }

        // ── Tournaments ────────────────────────────────────────────────────────

        public async Task<List<Tournament>> GetAllTournamentsAsync()
            => await _db.Tournaments.OrderBy(t => t.Name).ToListAsync();

        public async Task<Tournament?> GetTournamentBySlugAsync(string slug)
            => await _db.Tournaments.FirstOrDefaultAsync(t => t.Slug == slug);

        /// Takes back the wins that were awarded for these histories.
        private async Task RevokeWinsAsync(IEnumerable<PredictionsHistory> histories)
        {
            foreach (var h in histories.Where(h => h.WinsAwarded))
                foreach (var uid in WinsService.WinnerIds(h))   // needs h.Entries loaded
                {
                    var u = await _db.Users.FindAsync(uid);
                    if (u != null) u.TournamentsWon = Math.Max(0, u.TournamentsWon - 1);
                }
        }

        /// <summary>
        /// Creates a new tournament. Returns false if the slug is already taken.
        /// </summary>
        public async Task<bool> CreateTournamentAsync(string slug, string name, string icon, bool isActive, string pointsCalculationType = "Football", int? year = null, string? participatingCountryIds = null, string? sportType = null)
        {
            if (await _db.Tournaments.AnyAsync(t => t.Slug == slug))
                return false;

            _db.Tournaments.Add(new Tournament
            {
                Slug = slug,
                Name = name,
                Icon = icon,
                IsActive = isActive,
                PointsCalculationType = pointsCalculationType,
                Year = year ?? DateTime.Today.Year,
                ParticipatingCountryIds = participatingCountryIds,
                SportType = sportType
            });
            await _db.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Updates an existing tournament's fields.
        /// Returns false if the new slug is already taken by a different tournament.
        /// </summary>
        public async Task<bool> UpdateTournamentAsync(int id, string slug, string name, string icon, bool isActive, string pointsCalculationType = "Football", int? year = null, string? participatingCountryIds = null, string? sportType = null)
        {
            if (await _db.Tournaments.AnyAsync(t => t.Slug == slug && t.Id != id))
                return false;

            var t = await _db.Tournaments.FindAsync(id);
            if (t == null) return false;

            t.Slug = slug;
            t.Name = name;
            t.Icon = icon;
            t.IsActive = isActive;
            t.PointsCalculationType = pointsCalculationType;
            if (year.HasValue) t.Year = year.Value;
            t.ParticipatingCountryIds = participatingCountryIds;
            t.SportType = sportType;
            await _db.SaveChangesAsync();
            return true;
        }

        // ── Games ──────────────────────────────────────────────────────────────

        /// <summary>All games for a specific tournament, ordered by date.</summary>
        public async Task<List<Game>> GetMatchesByTournamentAsync(int tournamentId)
            => await _db.Games
                .Where(g => g.TournamentId == tournamentId)
                .OrderBy(g => g.GameDate)
                .ToListAsync();

        public async Task<List<Game>> GetAllMatchesAsync()
            => await _db.Games.OrderBy(g => g.GameDate).ToListAsync();

        public async Task AddMatchAsync(Game game)
        {
            game.GameDate = DateTime.SpecifyKind(game.GameDate, DateTimeKind.Local).ToUniversalTime();
            _db.Games.Add(game);
            await _db.SaveChangesAsync();
        }

        public async Task UpdateScoreAsync(int gameId, int homeScore, int awayScore, bool isOvertime = false)
        {
            var match = await _db.Games.FindAsync(gameId);
            if (match == null)
                return;

             match.HomeScore = homeScore;
             match.AwayScore = awayScore;
             match.IsOvertime = isOvertime;

            await _db.SaveChangesAsync();

            // A decisive score can settle a later round's placeholder slot (e.g. a semi-final feeding
            // the final) - re-check the whole bracket every time a score is saved.
            if (match.TournamentId.HasValue)
                await _playoffs.ResolveAsync(match.TournamentId.Value);
        }

        public async Task DeleteMatchAsync(int gameId)
        {
            var match = await _db.Games.FindAsync(gameId);
            if (match != null) { _db.Games.Remove(match); await _db.SaveChangesAsync(); }
        }

        public async Task<Game?> GetGameByIdAsync(int id) => await _db.Games.FindAsync(id);

        public async Task UpdateGameAsync(Game game)
        {
            var existing = await _db.Games.FindAsync(game.Id);
            if (existing != null)
            {
                existing.HomeTeam       = game.HomeTeam;
                existing.AwayTeam       = game.AwayTeam;
                existing.HomeCountryIds = game.HomeCountryIds;
                existing.AwayCountryIds = game.AwayCountryIds;
                existing.GameDate       = DateTime.SpecifyKind(game.GameDate, DateTimeKind.Local).ToUniversalTime();
                existing.Stage          = game.Stage;
                await _db.SaveChangesAsync();

                if (existing.TournamentId.HasValue)
                    await _playoffs.ResolveAsync(existing.TournamentId.Value);
            }
        }

        // ── Tournament Game Groups (for scheduling) ────────────────────────────

        /// <summary>All groups across every tournament — for the assignment admin section.</summary>
        public async Task<List<TournamentGroup>> GetAllGroupsAsync()
            => await _db.TournamentGroups
                .Include(g => g.Teams)
                .Include(g => g.Tournament)
                .OrderBy(g => g.Name)
                .ToListAsync();

        /// <summary>Groups belonging to a specific tournament.</summary>
        public async Task<List<TournamentGroup>> GetGroupsByTournamentAsync(int tournamentId)
            => await _db.TournamentGroups
                .Include(g => g.Teams)
                .Where(g => g.TournamentId == tournamentId)
                .OrderBy(g => g.Name)
                .ToListAsync();

        /// <summary>
        /// Saves a new group (with teams) and auto-generates round-robin games.
        /// TournamentId must be set on the group before calling.
        /// </summary>
        /// <param name="gamesPerPair">
        /// How many times each pair of teams plays - 1 for a normal round-robin, 2 for a double
        /// round-robin (home and away), etc. When more than 1, home/away alternates per meeting so
        /// no team hosts every game against the same opponent.
        /// </param>
        public async Task AddGroupWithGamesAsync(TournamentGroup group, int gamesPerPair = 1)
        {
            group.CreatedAt = DateTime.UtcNow;
            _db.TournamentGroups.Add(group);
            await _db.SaveChangesAsync(); // get group.Id

            var teams       = group.Teams.ToList();   // keep the GroupTeam objects, not just names, so CountryId carries through
            var placeholder = DateTime.UtcNow.Date;
            gamesPerPair     = Math.Max(1, gamesPerPair);

            for (int i = 0; i < teams.Count; i++)
            for (int j = i + 1; j < teams.Count; j++)
            for (int k = 0; k < gamesPerPair; k++)
            {
                bool swap = k % 2 == 1;   // alternate home/away across repeated meetings
                var home = swap ? teams[j] : teams[i];
                var away = swap ? teams[i] : teams[j];
                _db.Games.Add(new Game
                {
                    HomeTeam       = home.TeamName,
                    AwayTeam       = away.TeamName,
                    HomeCountryIds = home.CountryId?.ToString(),
                    AwayCountryIds = away.CountryId?.ToString(),
                    Stage          = group.Name,
                    GameDate       = placeholder,
                    GroupId        = group.Id,
                    TournamentId   = group.TournamentId
                });
            }
            await _db.SaveChangesAsync();
        }

        public async Task UpdateGroupTeamsAsync(int groupId, List<string> newTeamNames)
        {
            var group = await _db.TournamentGroups
                .Include(g => g.Teams)
                .Include(g => g.Games)
                .FirstOrDefaultAsync(g => g.Id == groupId);

            if (group == null) return;

            var oldTeams = group.Teams.OrderBy(t => t.Id).ToList();

            var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Math.Min(oldTeams.Count, newTeamNames.Count); i++)
            {
                var oldName = oldTeams[i].TeamName;
                var newName = newTeamNames[i].Trim();
                if (oldName != newName)
                    renameMap[oldName] = newName;
            }

            foreach (var kvp in renameMap)
            {
                var teamRecord = oldTeams.First(t => t.TeamName == kvp.Key);
                teamRecord.TeamName = kvp.Value;
            }

            foreach (var game in group.Games)
            {
                if (renameMap.TryGetValue(game.HomeTeam, out var newHome))
                    game.HomeTeam = newHome;
                if (renameMap.TryGetValue(game.AwayTeam, out var newAway))
                    game.AwayTeam = newAway;
            }

            await _db.SaveChangesAsync();
        }

        public async Task DeleteGroupAsync(int groupId)
        {
            var group = await _db.TournamentGroups.FindAsync(groupId);
            if (group != null) { _db.TournamentGroups.Remove(group); await _db.SaveChangesAsync(); }
        }

        public async Task AssignGroupToTournamentAsync(int groupId, int? tournamentId)
        {
            var group = await _db.TournamentGroups
                .Include(g => g.Games)
                .FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return;

            group.TournamentId = tournamentId;
            foreach (var game in group.Games)
                game.TournamentId = tournamentId;

            await _db.SaveChangesAsync();
        }

        // ── User Competition Groups ────────────────────────────────────────────

        /// <summary>Returns all user competition groups for a tournament, including their members.</summary>
        public async Task<List<UserGroup>> GetUserGroupsByTournamentAsync(int tournamentId)
            => await _db.UserGroups
                .Include(g => g.Members)
                    .ThenInclude(m => m.User)
                .Where(g => g.TournamentId == tournamentId)
                .OrderBy(g => g.Name)
                .ToListAsync();

        /// <summary>Creates a new user competition group for a tournament.</summary>
        public async Task CreateUserGroupAsync(string name, int tournamentId)
        {
            _db.UserGroups.Add(new UserGroup { Name = name.Trim(), TournamentId = tournamentId });
            await _db.SaveChangesAsync();
        }

        /// <summary>Deletes a user competition group and removes all its members.</summary>
        public async Task DeleteUserGroupAsync(int groupId)
        {
            var group = await _db.UserGroups.FindAsync(groupId);
            if (group != null) { _db.UserGroups.Remove(group); await _db.SaveChangesAsync(); }
        }

        /// <summary>
        /// Adds or removes a user from a competition group.
        /// isMember=true → add if not already present.
        /// isMember=false → remove if present.
        /// </summary>
        public async Task SetUserGroupMemberAsync(int groupId, int userId, bool isMember)
        {
            var existing = await _db.UserGroupMembers
                .FirstOrDefaultAsync(m => m.UserGroupId == groupId && m.UserId == userId);

            if (isMember && existing == null)
                _db.UserGroupMembers.Add(new UserGroupMember { UserGroupId = groupId, UserId = userId });
            else if (!isMember && existing != null)
                _db.UserGroupMembers.Remove(existing);

            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Returns the IDs of all users who share at least one competition group with
        /// the given user in the given tournament (including the user themselves).
        /// Returns null if the user is not in any group (meaning: show everyone).
        /// </summary>
        public async Task<List<int>?> GetGroupMateUserIdsAsync(int userId, int tournamentId)
        {
            var groupIds = await _db.UserGroupMembers
                .Where(m => m.UserId == userId && m.UserGroup.TournamentId == tournamentId)
                .Select(m => m.UserGroupId)
                .ToListAsync();

            if (!groupIds.Any()) return null; // user has no group → show all

            var userIds = await _db.UserGroupMembers
                .Where(m => groupIds.Contains(m.UserGroupId))
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync();

            return userIds;
        }

        /// <summary>
        /// Returns users in the same competition group(s) as the given user for this tournament.
        /// Returns all users if the current user has no group assigned.
        /// </summary>
        public async Task<List<User>> GetGroupMatesAsync(int userId, int tournamentId)
        {
            var groupMateIds = await GetGroupMateUserIdsAsync(userId, tournamentId);
            if (groupMateIds == null)
                return await _db.Users.OrderBy(u => u.Username).ToListAsync();

            return await _db.Users
                .Where(u => groupMateIds.Contains(u.Id))
                .OrderBy(u => u.Username)
                .ToListAsync();
        }

        // ── Predictions ────────────────────────────────────────────────────────

        public async Task SavePredictionAsync(int userId, int gameId, int homeScore, int awayScore, bool isOvertime = false)
        {
            var existing = await _db.Predictions.FirstOrDefaultAsync(p => p.UserId == userId && p.GameId == gameId);

            if (existing != null)
            {
                existing.PredictedHomeScore  = homeScore;
                existing.PredictedAwayScore  = awayScore;
                existing.PredictedIsOvertime = isOvertime;
            }
            else
            {
                _db.Predictions.Add(new Prediction
                {
                    UserId               = userId,
                    GameId               = gameId,
                    PredictedHomeScore   = homeScore,
                    PredictedAwayScore   = awayScore,
                    PredictedIsOvertime  = isOvertime,
                });
            }
            await _db.SaveChangesAsync();
        }

        public async Task<List<Prediction>> GetUserPredictionsByTournamentAsync(int userId, int tournamentId)
            => await _db.Predictions
                .Include(p => p.Game)
                .Where(p => p.UserId == userId && p.Game.TournamentId == tournamentId)
                .ToListAsync();

        // ── Leaderboard ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns leaderboard entries for a tournament.
        /// If filterUserIds is provided, only those users are included (for group filtering).
        /// </summary>
        public async Task<List<(User User, int Points)>> GetLeaderboardByTournamentAsync(
            int tournamentId, List<int>? filterUserIds = null, string? calcTypeOverride = null)
        {
            var tournament = await _db.Tournaments.FindAsync(tournamentId);
            var calcType = calcTypeOverride ?? tournament?.PointsCalculationType ?? "Football";  // NEW

            var query = _db.Users
                .Include(u => u.Predictions)
                    .ThenInclude(p => p.Game)
                .AsQueryable();

            if (filterUserIds != null)
                query = query.Where(u => filterUserIds.Contains(u.Id));

            var users = await query.ToListAsync();

            return users
                .Select(u => (
                    User: u,
                    Points: u.Predictions
                        .Where(p => p.Game.TournamentId == tournamentId)
                        .Sum(p =>
                        {
                            if (p.Game.HomeScore == null || p.Game.AwayScore == null) return 0;
                            return PointsCalculator.Calculate(
                                p.PredictedHomeScore, p.PredictedAwayScore, p.PredictedIsOvertime,
                                p.Game.HomeScore.Value, p.Game.AwayScore.Value, p.Game.IsOvertime,
                                p.Game.Stage,
                                calcType);
                        })
                ))
                .OrderByDescending(x => x.Points)
                .ToList();
        }

        /// <summary>
        /// Returns a dictionary of userId → count of perfectly predicted games
        /// (max points earned) for a tournament. Only 2 DB queries regardless of user count.
        /// </summary>
        public async Task<Dictionary<int, int>> GetPerfectCountsAsync(
            int tournamentId, List<int> userIds, string pointsCalculationType)
        {
            if (!userIds.Any())
                return new Dictionary<int, int>();

            // Query 1: all finished games for this tournament
            var finishedGames = await _db.Games
                .Where(g => g.TournamentId == tournamentId
                         && g.HomeScore != null
                         && g.AwayScore != null)
                .ToListAsync();

            if (!finishedGames.Any())
                return userIds.ToDictionary(id => id, _ => 0);

            var gameIds = finishedGames.Select(g => g.Id).ToList();

            // Query 2: all predictions from all relevant users for those games
            var allPredictions = await _db.Predictions
                .Where(p => userIds.Contains(p.UserId) && gameIds.Contains(p.GameId))
                .ToListAsync();

            bool ignoreOT = pointsCalculationType == "Football" || pointsCalculationType == "Football3";

            // Everything below is in-memory
            return userIds.ToDictionary(
                userId => userId,
                userId =>
                {
                    var userPreds = allPredictions.Where(p => p.UserId == userId).ToList();
                    return finishedGames.Count(g =>
                    {
                        var p = userPreds.FirstOrDefault(x => x.GameId == g.Id);
                        if (p == null) return false;

                        bool scoresMatch = p.PredictedHomeScore == g.HomeScore!.Value && p.PredictedAwayScore == g.AwayScore!.Value;
                        bool otMatch = ignoreOT || p.PredictedIsOvertime == g.IsOvertime;

                        return scoresMatch && otMatch;
                    });
                });
        }

        // ── Users ──────────────────────────────────────────────────────────────

        public async Task<List<User>> GetAllUsersAsync() => await _db.Users.OrderBy(u => u.Username).ToListAsync();

        // --- Api ----------------------------------------------------------------
        public async Task<ApiSportsConfig?> GetApiConfigAsync(int tournamentId)
    => await _db.ApiSportsConfigs.FirstOrDefaultAsync(c => c.TournamentId == tournamentId);

        public async Task SaveApiConfigAsync(ApiSportsConfig config)
        {
            var existing = await _db.ApiSportsConfigs
                .FirstOrDefaultAsync(c => c.TournamentId == config.TournamentId);
            if (existing == null)
                _db.ApiSportsConfigs.Add(config);
            else
            {
                existing.ApiKey = config.ApiKey;
                existing.LeagueId = config.LeagueId;
                existing.LeagueName = config.LeagueName;
                existing.SeasonYear = config.SeasonYear;
                existing.IsEnabled = config.IsEnabled;
            }
            await _db.SaveChangesAsync();
        }

        public async Task<List<ApiTeamMapping>> GetApiTeamMappingsAsync(int tournamentId)
            => await _db.ApiTeamMappings
                .Where(m => m.TournamentId == tournamentId)
                .ToListAsync();

        public async Task SaveApiTeamMappingAsync(int tournamentId, string localName,
                                                   int apiTeamId, string apiTeamName)
        {
            var existing = await _db.ApiTeamMappings
                .FirstOrDefaultAsync(m => m.TournamentId == tournamentId
                                       && m.LocalTeamName == localName);
            if (existing == null)
                _db.ApiTeamMappings.Add(new ApiTeamMapping
                {
                    TournamentId = tournamentId,
                    LocalTeamName = localName,
                    ApiTeamId = apiTeamId,
                    ApiTeamName = apiTeamName
                });
            else
            {
                existing.ApiTeamId = apiTeamId;
                existing.ApiTeamName = apiTeamName;
            }
            await _db.SaveChangesAsync();
        }

        public async Task DeleteApiTeamMappingAsync(int tournamentId, string localName)
        {
            var m = await _db.ApiTeamMappings
                .FirstOrDefaultAsync(m => m.TournamentId == tournamentId
                                       && m.LocalTeamName == localName);
            if (m != null)
            {
                _db.ApiTeamMappings.Remove(m);
                await _db.SaveChangesAsync();
            }
        }

        public async Task<List<ApiSyncLog>> GetApiSyncLogsAsync(int tournamentId)
            => await _db.ApiSyncLogs
                .Where(l => l.TournamentId == tournamentId)
                .OrderByDescending(l => l.RunAt)
                .Take(100)
                .ToListAsync();

        // ── PredictionsHistory ─────────────────────────────────────────────────

        /// <summary>
        /// Calculates points for every user who made predictions in the tournament,
        /// creates a PredictionsHistory record, and returns it.
        /// </summary>
        public async Task<List<PredictionsHistory>> CompleteTournamentHistoryAsync(int tournamentId)
        {
            var tournament = await _db.Tournaments.FindAsync(tournamentId) ?? throw new InvalidOperationException("Tournament not found.");

            // Re-completing replaces earlier histories of this tournament instead of duplicating them
            var old = await _db.PredictionsHistories
                .Include(h => h.Entries)
                .Where(h => h.TournamentId == tournamentId)
                .ToListAsync();
            if (old.Any())
            {
                await RevokeWinsAsync(old);
                _db.PredictionsHistories.RemoveRange(old);
            }

            var userGroups = await GetUserGroupsByTournamentAsync(tournamentId);
            var results = new List<PredictionsHistory>();

            if (userGroups.Any())
            {
                foreach (var ug in userGroups)
                {
                    var memberIds = ug.Members.Select(m => m.UserId).ToList();
                    var calcType = ug.PointsCalculationType ?? tournament.PointsCalculationType;
                    var leaderboard = await GetLeaderboardByTournamentAsync(tournamentId, memberIds, calcType);

                    var history = new PredictionsHistory
                    {
                        TournamentId = tournamentId,
                        TournamentName = $"{tournament.Name} – {ug.Name}",
                        CompletedAt = DateTime.UtcNow,
                        Entries = leaderboard
                            .Where(x => x.Points > 0)
                            .Select(x => new PredictionsHistoryEntry
                            {
                                UserId = x.User.Id,
                                PlayerName = x.User.Username,
                                Points = x.Points
                            })
                            .ToList()
                    };

                    _db.PredictionsHistories.Add(history);
                    results.Add(history);
                }
            }
            else
            {
                // No user groups — old behaviour, one entry for everyone
                var leaderboard = await GetLeaderboardByTournamentAsync(tournamentId);
                var history = new PredictionsHistory
                {
                    TournamentId = tournamentId,
                    TournamentName = tournament.Name,
                    CompletedAt = DateTime.UtcNow,
                    Entries = leaderboard
                        .Where(x => x.Points > 0)
                        .Select(x => new PredictionsHistoryEntry
                        {
                            UserId = x.User.Id,
                            PlayerName = x.User.Username,
                            Points = x.Points
                        })
                        .ToList()
                };
                _db.PredictionsHistories.Add(history);
                results.Add(history);
            }

            await _db.SaveChangesAsync();

            // A completed tournament no longer accepts predictions / shows as "current".
            tournament.IsActive = false;
            await _db.SaveChangesAsync();

            return results;
        }

        /// <summary>
        /// Saves a manually-entered history record (for external tournaments).
        /// </summary>
        public async Task<PredictionsHistory> SaveManualHistoryAsync(
            string tournamentName, List<(string playerName, int? userId, int points)> entries)
        {
            var history = new PredictionsHistory
            {
                TournamentName = tournamentName,
                CompletedAt    = DateTime.UtcNow,
                Entries        = entries.Select(e => new PredictionsHistoryEntry
                {
                    UserId     = e.userId,
                    PlayerName = e.playerName,
                    Points     = e.points
                }).ToList()
            };

            _db.PredictionsHistories.Add(history);
            await _db.SaveChangesAsync();
            return history;
        }

        /// <summary>All history records ordered by Id DESC, with entries included.</summary>
        public async Task<List<PredictionsHistory>> GetAllHistoryAsync()
            => await _db.PredictionsHistories
                .Include(h => h.Entries)
                    .ThenInclude(e => e.User)
                .OrderByDescending(h => h.Id)
                .ToListAsync();

        public async Task DeleteHistoryAsync(int historyId)
        {
            var h = await _db.PredictionsHistories
                .Include(x => x.Entries)
                .FirstOrDefaultAsync(x => x.Id == historyId);
            if (h == null) return;
            await RevokeWinsAsync(new[] { h });
            _db.PredictionsHistories.Remove(h);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Returns each UserGroup the given user belongs to for this tournament,
        /// with all member user IDs. Used for per-group leaderboard filtering.
        /// </summary>
        public async Task<List<UserGroup>> GetUserGroupsForUserAsync(int userId, int tournamentId)
        {
            var groupIds = await _db.UserGroupMembers
                .Where(m => m.UserId == userId && m.UserGroup.TournamentId == tournamentId)
                .Select(m => m.UserGroupId)
                .ToListAsync();

            if (!groupIds.Any()) return new List<UserGroup>();

            return await _db.UserGroups
                .Include(g => g.Members)
                .Where(g => groupIds.Contains(g.Id))
                .OrderBy(g => g.Name)
                .ToListAsync();
        }

        public async Task SetUserGroupCalcTypeAsync(int groupId, string? calcType)
        {
            var group = await _db.UserGroups.FindAsync(groupId);
            if (group == null) return;
            group.PointsCalculationType = calcType;
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// One-time migration: for predictions where main score is 0/0 (never set)
        /// but FullTime fields were filled, copy FullTime → main score fields.
        /// Safe to run multiple times — only touches rows where PredictedHomeFullTime != null.
        /// </summary>
        public async Task MigrateFullTimePredictionsAsync()
        {
            var predictions = await _db.Predictions
                .Where(p => p.PredictedHomeFullTime != null
                         && p.PredictedAwayFullTime != null
                         && p.PredictedHomeScore == 0
                         && p.PredictedAwayScore == 0)
                .ToListAsync();

            foreach (var p in predictions)
            {
                p.PredictedHomeScore = p.PredictedHomeFullTime!.Value;
                p.PredictedAwayScore = p.PredictedAwayFullTime!.Value;
            }

            await _db.SaveChangesAsync();
        }

        /// <summary>All history entries for a specific user, newest first.</summary>
        public async Task<List<(PredictionsHistory History, PredictionsHistoryEntry Entry, int TotalParticipants)>>
            GetUserTournamentHistoryAsync(int userId)
        {
            var histories = await _db.PredictionsHistories
                .Include(h => h.Entries)
                .Where(h => h.Entries.Any(e => e.UserId == userId))
                .OrderByDescending(h => h.CompletedAt)
                .ToListAsync();

            return histories.Select(h =>
            {
                var entry = h.Entries.First(e => e.UserId == userId);
                var total = h.Entries.Count;
                var sorted = h.Entries.OrderByDescending(e => e.Points).ToList();
                return (h, entry, total);
            }).ToList();
        }

        /// <summary>Per-stage prediction breakdown for a user in a tournament.</summary>
        public async Task<List<(string Stage, int Predicted, int Total, int Points)>>
            GetUserStageStatsAsync(int userId, int tournamentId, string calcType)
        {
            var games = await _db.Games
                .Where(g => g.TournamentId == tournamentId
                         && g.HomeScore != null
                         && g.AwayScore != null)
                .ToListAsync();

            var gameIds = games.Select(g => g.Id).ToList();
            var preds = await _db.Predictions
                .Where(p => p.UserId == userId && gameIds.Contains(p.GameId))
                .ToListAsync();

            return games
                .GroupBy(g => g.Stage)
                .OrderBy(grp => PredictionHelper.StageOrder(grp.Key))
                .Select(grp =>
                {
                    int total = grp.Count();
                    int predicted = grp.Count(g =>
                    {
                        var p = preds.FirstOrDefault(x => x.GameId == g.Id);
                        return p != null;
                    });
                    int points = grp.Sum(g =>
                    {
                        var p = preds.FirstOrDefault(x => x.GameId == g.Id);
                        if (p == null) return 0;
                        return PointsCalculator.Calculate(
                            p.PredictedHomeScore, p.PredictedAwayScore, p.PredictedIsOvertime,
                            g.HomeScore!.Value, g.AwayScore!.Value, g.IsOvertime,
                            g.Stage, calcType);
                    });
                    return (grp.Key, predicted, total, points);
                })
                .ToList();
        }

        public async Task<List<(Game Game, Prediction? Pred, int? Points)>>
    GetUserPredictionDetailAsync(int userId, int tournamentId, string calcType)
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                            TimeZoneInfo.FindSystemTimeZoneById("Europe/Riga"));

            var games = await _db.Games
                .Where(g => g.TournamentId == tournamentId)
                .OrderBy(g => g.GameDate)
                .ToListAsync();

            var gameIds = games.Select(g => g.Id).ToList();
            var preds = await _db.Predictions
                .Where(p => p.UserId == userId && gameIds.Contains(p.GameId))
                .ToListAsync();

            return games.Select(g =>
            {
                // Hide prediction for games that haven't started yet
                bool started = g.GameDate < now;

                var p = started ? preds.FirstOrDefault(x => x.GameId == g.Id) : null;
                int? pts = null;
                if (p != null && g.HomeScore != null && g.AwayScore != null)
                    pts = PointsCalculator.Calculate(
                        p.PredictedHomeScore, p.PredictedAwayScore, p.PredictedIsOvertime,
                        g.HomeScore.Value, g.AwayScore.Value, g.IsOvertime,
                        g.Stage, calcType);
                return (g, (Prediction?)p, pts);
            }).ToList();
        }
    }


}
