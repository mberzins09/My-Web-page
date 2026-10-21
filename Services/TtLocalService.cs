using MartinsWeb.Data;
using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Services
{
    /// <summary>All reads/writes for the local table tennis tournaments (stored in app.db).</summary>
    public class TtLocalService
    {
        private readonly AppDbContext _db;
        public TtLocalService(AppDbContext db) => _db = db;

        // ══ Players ══════════════════════════════════════════════════════════
        public Task<List<PlayerTT>> GetPlayersAsync() =>
            _db.PlayersTT.AsNoTracking().Include(p => p.User)
               .OrderBy(p => p.Surname).ThenBy(p => p.Name).ToListAsync();

        public Task<List<User>> GetUsersAsync() =>
            _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();

        /// <summary>Returns an error message, or null on success.</summary>
        public async Task<string?> SavePlayerAsync(int? id, string name, string surname, int? userId)
        {
            name = name.Trim(); surname = surname.Trim();
            if (name.Length == 0 || surname.Length == 0) return "Name and surname are required.";

            if (userId != null && await _db.PlayersTT.AnyAsync(p => p.UserId == userId && p.Id != (id ?? 0)))
                return "That user is already linked to another player.";

            PlayerTT p;
            if (id == null) { p = new PlayerTT(); _db.PlayersTT.Add(p); }
            else p = await _db.PlayersTT.FindAsync(id.Value) ?? throw new InvalidOperationException("Player not found.");

            p.Name = name; p.Surname = surname; p.UserId = userId;
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task<string?> DeletePlayerAsync(int id)
        {
            if (await _db.TournamentPlayersTT.AnyAsync(p => p.PlayerId == id) ||
                await _db.GamesTT.AnyAsync(g => g.Player1Id == id || g.Player2Id == id))
                return "This player takes part in a tournament and can't be deleted. Remove them from the tournaments first.";
            var p = await _db.PlayersTT.FindAsync(id);
            if (p != null) { _db.PlayersTT.Remove(p); await _db.SaveChangesAsync(); }
            return null;
        }

        // ══ Seasons ══════════════════════════════════════════════════════════
        public Task<List<TournamentSeason>> GetSeasonsAsync() =>
            _db.TournamentSeasons.AsNoTracking().Include(s => s.Tournaments)
               .OrderByDescending(s => s.Id).ToListAsync();

        public async Task<string?> SaveSeasonAsync(int? id, string name)
        {
            name = name.Trim();
            if (name.Length == 0) return "Season name is required.";
            TournamentSeason s;
            if (id == null) { s = new TournamentSeason(); _db.TournamentSeasons.Add(s); }
            else s = await _db.TournamentSeasons.FindAsync(id.Value) ?? throw new InvalidOperationException("Season not found.");
            s.Name = name;
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>Tournaments in the season are kept - they just become "no season".</summary>
        public async Task DeleteSeasonAsync(int id)
        {
            var s = await _db.TournamentSeasons.Include(x => x.Tournaments).FirstOrDefaultAsync(x => x.Id == id);
            if (s == null) return;
            foreach (var t in s.Tournaments) t.TournamentSeasonId = null;
            _db.TournamentSeasons.Remove(s);
            await _db.SaveChangesAsync();
        }

        // ══ Tournaments ══════════════════════════════════════════════════════
        public Task<List<TournamentTT>> GetTournamentsAsync() =>
            _db.TournamentsTT.AsNoTracking().Include(t => t.TournamentSeason)
               .Include(t => t.Games)
               .OrderByDescending(t => t.TournamentDateTime).ToListAsync();

        /// <summary>Tournament without participants/games.</summary>
        public Task<TournamentTT?> GetTournamentAsync(int id) =>
            _db.TournamentsTT.AsNoTracking().Include(t => t.TournamentSeason)
               .FirstOrDefaultAsync(t => t.Id == id);

        /// <summary>Tournament with participants, games, set points and players.</summary>
        public Task<TournamentTT?> GetTournamentFullAsync(int id) =>
            _db.TournamentsTT.AsNoTracking().AsSplitQuery()
               .Include(t => t.TournamentSeason)
               .Include(t => t.Participants).ThenInclude(p => p.Player)
               .Include(t => t.Games).ThenInclude(g => g.Sets)
               .Include(t => t.Games).ThenInclude(g => g.Player1)
               .Include(t => t.Games).ThenInclude(g => g.Player2)
               .FirstOrDefaultAsync(t => t.Id == id);

        /// <summary>Returns (error, savedId). Error is null on success.</summary>
        public async Task<(string? Error, int Id)> SaveTournamentAsync(TournamentTT input)
        {
            if (string.IsNullOrWhiteSpace(input.TournamentName)) return ("Tournament name is required.", input.Id);
            if (input.GroupCount < 1 || input.GroupCount > 8) return ("Group count must be between 1 and 8.", input.Id);
            if (input.QualifiersPerGroup < 1 || input.QualifiersPerGroup > 16) return ("Qualifiers per group must be between 1 and 16.", input.Id);
            if (input.SetsToWinGroup < 1 || input.SetsToWinGroup > 5 || input.SetsToWinPlayoff < 1 || input.SetsToWinPlayoff > 5)
                return ("Sets to win must be between 1 and 5.", input.Id);

            // placement blocks only make sense when a group stage feeds the playoff
            if (input.Format != TtTournamentFormat.GroupStageAndPlayoff && input.PlayoffType == TtPlayoffType.PlacementBlocks)
                input.PlayoffType = TtPlayoffType.AllPlaces;

            TournamentTT t;
            if (input.Id == 0) { t = new TournamentTT(); _db.TournamentsTT.Add(t); }
            else
            {
                t = await _db.TournamentsTT.FindAsync(input.Id) ?? throw new InvalidOperationException("Tournament not found.");
                if ((t.PlayoffType != input.PlayoffType || t.Format != input.Format) && await HasPlayoffGamesAsync(t.Id))
                    return ("The playoff has already been generated - delete it (Games page) before changing the format or the playoff type.", input.Id);
            }

            t.TournamentName     = input.TournamentName.Trim();
            t.TournamentDateTime = input.TournamentDateTime;
            t.Format             = input.Format;
            t.TournamentSeasonId = input.TournamentSeasonId;
            t.GroupCount         = input.GroupCount;
            t.QualifiersPerGroup = input.QualifiersPerGroup;
            t.SetsToWinGroup     = input.SetsToWinGroup;
            t.SetsToWinPlayoff   = input.SetsToWinPlayoff;
            t.PlayoffType        = input.PlayoffType;
            await _db.SaveChangesAsync();
            return (null, t.Id);
        }

        /// <summary>Deletes the tournament with all participants, games and set points (cascade).</summary>
        public async Task DeleteTournamentAsync(int id)
        {
            var t = await _db.TournamentsTT.FindAsync(id);
            if (t != null) { _db.TournamentsTT.Remove(t); await _db.SaveChangesAsync(); }
        }

        // ══ Participants ═════════════════════════════════════════════════════
        public Task<List<TournamentPlayerTT>> GetParticipantsAsync(int tournamentId) =>
            _db.TournamentPlayersTT.AsNoTracking().Include(p => p.Player)
               .Where(p => p.TournamentId == tournamentId)
               .OrderBy(p => p.GroupNumber ?? 999).ThenBy(p => p.Seed ?? 999).ThenBy(p => p.Id).ToListAsync();

        public async Task<string?> AddParticipantAsync(int tournamentId, int playerId)
        {
            if (await _db.TournamentPlayersTT.AnyAsync(p => p.TournamentId == tournamentId && p.PlayerId == playerId))
                return "That player is already in the tournament.";
            int nextSeed = (await _db.TournamentPlayersTT.Where(p => p.TournamentId == tournamentId)
                                .MaxAsync(p => (int?)p.Seed) ?? 0) + 1;
            _db.TournamentPlayersTT.Add(new TournamentPlayerTT { TournamentId = tournamentId, PlayerId = playerId, Seed = nextSeed });
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task<string?> RemoveParticipantAsync(int participantId)
        {
            var p = await _db.TournamentPlayersTT.FindAsync(participantId);
            if (p == null) return null;
            if (await _db.GamesTT.AnyAsync(g => g.TournamentId == p.TournamentId &&
                                                (g.Player1Id == p.PlayerId || g.Player2Id == p.PlayerId)))
                return "This player already has games in the tournament. Delete the games first.";
            _db.TournamentPlayersTT.Remove(p);
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task<string?> SetGroupAsync(int participantId, int? group)
        {
            var p = await _db.TournamentPlayersTT.FindAsync(participantId);
            if (p == null) return null;
            if (await _db.GamesTT.AnyAsync(g => g.TournamentId == p.TournamentId && g.Stage == TtLocalLabels.StageGroup &&
                                                (g.Player1Id == p.PlayerId || g.Player2Id == p.PlayerId)))
                return "This player already has group games. Delete the group games before changing groups.";
            p.GroupNumber = group;
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task SetSeedAsync(int participantId, int? seed)
        {
            var p = await _db.TournamentPlayersTT.FindAsync(participantId);
            if (p == null) return;
            p.Seed = seed;
            await _db.SaveChangesAsync();
        }

        /// <summary>Random draw into the tournament's groups (snake distribution, so groups are equal in size).</summary>
        public async Task<string?> AutoDrawGroupsAsync(int tournamentId)
        {
            var t = await _db.TournamentsTT.FindAsync(tournamentId);
            if (t == null || !t.HasGroups) return "This tournament has no group stage.";
            if (await _db.GamesTT.AnyAsync(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StageGroup))
                return "Group games already exist. Delete the group games before drawing groups again.";

            var parts = await _db.TournamentPlayersTT.Where(p => p.TournamentId == tournamentId).ToListAsync();
            if (parts.Count < t.GroupCount) return "There are fewer players than groups.";

            var shuffled = parts.OrderBy(_ => Random.Shared.Next()).ToList();
            int n = t.GroupCount;
            for (int i = 0; i < shuffled.Count; i++)
            {
                int cycle = i / n, idx = i % n;
                shuffled[i].GroupNumber = (cycle % 2 == 0 ? idx : n - 1 - idx) + 1;
            }
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task ShuffleSeedsAsync(int tournamentId)
        {
            var parts = await _db.TournamentPlayersTT.Where(p => p.TournamentId == tournamentId).ToListAsync();
            var shuffled = parts.OrderBy(_ => Random.Shared.Next()).ToList();
            for (int i = 0; i < shuffled.Count; i++) shuffled[i].Seed = i + 1;
            await _db.SaveChangesAsync();
        }

        // ══ Games ════════════════════════════════════════════════════════════
        public Task<List<GameTT>> GetGamesAsync(int tournamentId) =>
            _db.GamesTT.AsNoTracking().AsSplitQuery()
               .Include(g => g.Player1).Include(g => g.Player2).Include(g => g.Sets)
               .Where(g => g.TournamentId == tournamentId)
               .OrderBy(g => g.Stage).ThenBy(g => g.GroupNumber).ThenBy(g => g.Round).ThenBy(g => g.Position).ThenBy(g => g.Id)
               .ToListAsync();

        /// <summary>Round-robin schedule for every group (circle method). Existing pairs are skipped.</summary>
        public async Task<(string? Error, int Created)> GenerateGroupGamesAsync(int tournamentId)
        {
            var t = await _db.TournamentsTT.FindAsync(tournamentId);
            if (t == null) return ("Tournament not found.", 0);
            if (!t.HasGroups) return ("This tournament has no group stage.", 0);

            var parts = await _db.TournamentPlayersTT.AsNoTracking().Where(p => p.TournamentId == tournamentId).ToListAsync();
            if (parts.Count < 2) return ("Add at least two participants first.", 0);
            if (parts.Any(p => p.GroupNumber == null)) return ("Assign every participant to a group first.", 0);

            var existing = await _db.GamesTT.Where(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StageGroup)
                                            .Select(g => new { g.Player1Id, g.Player2Id }).ToListAsync();
            var existingPairs = existing.Select(g => PairKey(g.Player1Id!.Value, g.Player2Id!.Value)).ToHashSet();

            int created = 0;
            foreach (var grp in parts.GroupBy(p => p.GroupNumber!.Value).OrderBy(g => g.Key))
            {
                var ids = grp.OrderBy(p => p.Seed ?? int.MaxValue).ThenBy(p => p.Id).Select(p => p.PlayerId).ToList();
                if (ids.Count < 2) continue;
                foreach (var (round, a, b) in RoundRobin(ids))
                {
                    if (!existingPairs.Add(PairKey(a, b))) continue;
                    _db.GamesTT.Add(new GameTT
                    {
                        TournamentId = tournamentId, Stage = TtLocalLabels.StageGroup,
                        GroupNumber = grp.Key, Round = round, Player1Id = a, Player2Id = b
                    });
                    created++;
                }
            }
            await _db.SaveChangesAsync();
            return (null, created);
        }

        /// <summary>Adds one extra group game by hand (both players must be in the same group).</summary>
        public async Task<string?> CreateGroupGameAsync(int tournamentId, int player1Id, int player2Id)
        {
            if (player1Id == 0 || player2Id == 0) return "Pick both players.";
            if (player1Id == player2Id) return "A player can't play against themselves.";

            var parts = await _db.TournamentPlayersTT.AsNoTracking()
                .Where(p => p.TournamentId == tournamentId && (p.PlayerId == player1Id || p.PlayerId == player2Id)).ToListAsync();
            if (parts.Count != 2) return "Both players must be participants of this tournament.";
            if (parts[0].GroupNumber != parts[1].GroupNumber) return "The players are in different groups.";

            bool dup = await _db.GamesTT.AnyAsync(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StageGroup &&
                ((g.Player1Id == player1Id && g.Player2Id == player2Id) || (g.Player1Id == player2Id && g.Player2Id == player1Id)));
            if (dup) return "These two players already have a game.";

            _db.GamesTT.Add(new GameTT
            {
                TournamentId = tournamentId, Stage = TtLocalLabels.StageGroup,
                GroupNumber = parts[0].GroupNumber, Player1Id = player1Id, Player2Id = player2Id
            });
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>Deletes a single group game. Playoff games are removed with DeleteStageGamesAsync.</summary>
        public async Task<string?> DeleteGameAsync(int id)
        {
            var g = await _db.GamesTT.FindAsync(id);
            if (g == null) return null;
            if (g.Stage != TtLocalLabels.StageGroup) return "Playoff games can only be deleted together (delete the whole playoff).";
            if (await HasPlayoffGamesAsync(g.TournamentId))
                return "Delete the playoff first - it was built from the group results.";
            _db.GamesTT.Remove(g);
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>True if bracket games or additional-group games exist.</summary>
        private Task<bool> HasPlayoffGamesAsync(int tournamentId) =>
            _db.GamesTT.AnyAsync(g => g.TournamentId == tournamentId &&
                (g.Stage == TtLocalLabels.StagePlayoff || g.Stage == TtLocalLabels.StageBlockGroup));

        public async Task<string?> DeleteStageGamesAsync(int tournamentId, string stage)
        {
            if (stage == TtLocalLabels.StageGroup && await HasPlayoffGamesAsync(tournamentId))
                return "Delete the playoff first - it was built from the group results.";

            // "the playoff" = bracket games and the games of additional groups
            var games = await _db.GamesTT.Where(g => g.TournamentId == tournamentId &&
                (g.Stage == stage || (stage == TtLocalLabels.StagePlayoff && g.Stage == TtLocalLabels.StageBlockGroup))).ToListAsync();
            _db.GamesTT.RemoveRange(games);   // set points go with them (cascade)
            if (stage == TtLocalLabels.StagePlayoff)
                foreach (var tp in await _db.TournamentPlayersTT.Where(p => p.TournamentId == tournamentId).ToListAsync())
                { tp.PlayoffSeed = null; tp.PlayoffBlock = null; }
            await _db.SaveChangesAsync();
            return null;
        }

        // ══ Results ══════════════════════════════════════════════════════════
        /// <summary>
        /// Saves a game result. If any set points are given, the sets won are calculated from them.
        /// Otherwise manualP1/manualP2 (sets won) are used and no set points are stored.
        /// In a playoff the winner (and, in an all-places playoff, the loser too) is moved to the next game automatically.
        /// </summary>
        public async Task<string?> SaveResultAsync(int gameId, int? manualP1, int? manualP2,
            IReadOnlyList<(int? P1, int? P2)> setPoints)
        {
            var g = await _db.GamesTT.Include(x => x.Sets).Include(x => x.Tournament)
                                     .FirstOrDefaultAsync(x => x.Id == gameId);
            if (g == null) return "Game not found.";
            if (g.IsBye) return "A bye game has no result - the player passes automatically.";
            if (g.Player1Id == null || g.Player2Id == null) return "Both players must be known before a result can be entered.";

            int toWin = g.Stage == TtLocalLabels.StagePlayoff ? g.Tournament!.SetsToWinPlayoff : g.Tournament!.SetsToWinGroup;

            // 1) read the set rows (empty rows are skipped, but no gaps allowed)
            var rows = new List<(int P1, int P2)>();
            bool gap = false;
            for (int i = 0; i < setPoints.Count; i++)
            {
                var (a, b) = setPoints[i];
                if (a == null && b == null) { gap = true; continue; }
                if (gap) return $"Set {i + 1} has points but an earlier set is empty.";
                if (a == null || b == null) return $"Set {i + 1}: enter the points of both players (or leave both empty).";
                if (a < 0 || b < 0) return $"Set {i + 1}: points can't be negative.";
                if (a == b) return $"Set {i + 1}: a set can't end in a tie.";
                rows.Add((a.Value, b.Value));
            }

            // 2) sets won
            int s1, s2;
            if (rows.Count > 0)
            {
                s1 = rows.Count(r => r.P1 > r.P2);
                s2 = rows.Count - s1;
            }
            else
            {
                if (manualP1 == null || manualP2 == null) return "Enter the sets won by each player (or the points of the sets).";
                if (manualP1 < 0 || manualP2 < 0) return "Sets can't be negative.";
                s1 = manualP1.Value; s2 = manualP2.Value;
            }
            if (s1 == s2 || Math.Max(s1, s2) != toWin)
                return $"This game is played {TtLocalLabels.SetsToWin(toWin)}: the winner needs exactly {toWin} set(s) and the other player fewer. You entered {s1}:{s2}.";

            int newWinner = s1 > s2 ? g.Player1Id!.Value : g.Player2Id!.Value;
            int newLoser  = s1 > s2 ? g.Player2Id!.Value : g.Player1Id!.Value;
            int? oldWinner = g.WinnerId;     // before this save (null if not played yet)

            // 3) playoff: players only move on if the winner changed; refuse if a later real game is already played
            PlayoffMap? pm = null;
            bool moves = g.Stage == TtLocalLabels.StagePlayoff && g.Round != null && g.Position != null && oldWinner != newWinner;
            if (moves)
            {
                pm = await LoadPlayoffMapAsync(g.TournamentId, g.Block, g.Tournament!.BracketType);
                var err = pm.CheckDownstream(g);
                if (err != null) return err;
            }

            // 4) save
            g.Player1Sets = s1; g.Player2Sets = s2;
            _db.GameSetsTT.RemoveRange(g.Sets.ToList());
            g.Sets.Clear();
            for (int i = 0; i < rows.Count; i++)
                g.Sets.Add(new GameSetTT { SetNumber = i + 1, Player1Points = rows[i].P1, Player2Points = rows[i].P2 });

            if (pm != null)
            {
                pm.Place(g, true, newWinner);
                pm.Place(g, false, newLoser);
            }
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>Marks a game as not played again (removes sets and set points).</summary>
        public async Task<string?> ClearResultAsync(int gameId)
        {
            var g = await _db.GamesTT.Include(x => x.Sets).Include(x => x.Tournament)
                                     .FirstOrDefaultAsync(x => x.Id == gameId);
            if (g == null) return null;
            if (g.IsBye) return null;

            PlayoffMap? pm = null;
            if (g.Stage == TtLocalLabels.StagePlayoff && g.IsFinished)
            {
                pm = await LoadPlayoffMapAsync(g.TournamentId, g.Block, g.Tournament!.BracketType);
                var err = pm.CheckDownstream(g);
                if (err != null) return err;
            }

            g.Player1Sets = null; g.Player2Sets = null;
            _db.GameSetsTT.RemoveRange(g.Sets.ToList());
            g.Sets.Clear();
            if (pm != null)
            {
                pm.Place(g, true, null);
                pm.Place(g, false, null);
            }
            await _db.SaveChangesAsync();
            return null;
        }

        private async Task<PlayoffMap> LoadPlayoffMapAsync(int tournamentId, int? block, TtPlayoffType type)
        {
            var games = await _db.GamesTT.Where(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StagePlayoff &&
                                                      g.Block == block && g.Round != null && g.Position != null).ToListAsync();
            return new PlayoffMap(type, games);
        }

        // ══ Playoff ══════════════════════════════════════════════════════════
        /// <summary>
        /// Builds the playoff.
        ///  - Playoff only: the participants' seeds.
        ///  - Groups + playoff, single elimination / all places: the top QualifiersPerGroup of every group qualify
        ///    (all group winners are seeded first, then all runners-up, ...).
        ///  - Groups + playoff, placement blocks: places 1..Q of every group form block 1, the next Q places block 2, ...
        ///    A block whose size is a power of two gets an all-places bracket, any other block is played as an
        ///    additional group (games already played in the group stage are carried over).
        /// In round 1 players of the same group never meet if the draw can avoid it.
        /// </summary>
        public async Task<string?> GeneratePlayoffAsync(int tournamentId)
        {
            var t = await _db.TournamentsTT.FindAsync(tournamentId);
            if (t == null) return "Tournament not found.";
            if (!t.HasPlayoff) return "This tournament has no playoff.";
            if (await HasPlayoffGamesAsync(tournamentId))
                return "The playoff already exists. Delete it first if you want to generate it again.";

            var parts = await _db.TournamentPlayersTT.AsNoTracking().Include(p => p.Player)
                                  .Where(p => p.TournamentId == tournamentId).ToListAsync();

            var seedMap  = new Dictionary<int, int>();   // playerId -> seed in his bracket
            var blockMap = new Dictionary<int, int>();   // playerId -> placement block
            var newGames = new List<GameTT>();

            if (t.Format == TtTournamentFormat.PlayoffOnly)
            {
                var ids = parts.OrderBy(p => p.Seed ?? int.MaxValue).ThenBy(p => p.Id).Select(p => p.PlayerId).ToList();
                if (ids.Count < 2) return "At least 2 players are needed for a playoff.";
                for (int i = 0; i < ids.Count; i++) seedMap[ids[i]] = i + 1;
                newGames.AddRange(BuildBracketGames(t, ids, null));
            }
            else
            {
                var groupGames = await _db.GamesTT.AsNoTracking().Include(g => g.Sets)
                    .Where(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StageGroup).ToListAsync();
                if (groupGames.Count == 0) return "Generate and play the group games first.";
                int unfinished = groupGames.Count(g => !g.IsFinished);
                if (unfinished > 0) return $"{unfinished} group game(s) still have no result.";

                var perGroup = parts.Where(p => p.GroupNumber != null).GroupBy(p => p.GroupNumber!.Value)
                    .ToDictionary(g => g.Key,
                                  g => BuildStandings(g.Select(p => p.Player!),
                                                      groupGames.Where(x => x.GroupNumber == g.Key)));

                if (t.PlayoffType == TtPlayoffType.PlacementBlocks)
                {
                    var error = BuildPlacementBlocks(t, perGroup, groupGames, seedMap, blockMap, newGames);
                    if (error != null) return error;
                }
                else
                {
                    var ranked = RankedQualifiers(perGroup, 0, t.QualifiersPerGroup);
                    if (ranked.Count < 2) return "At least 2 players are needed for a playoff.";
                    var ids = AssignSeeds(ranked, BracketSize(ranked.Count));
                    for (int i = 0; i < ids.Count; i++) seedMap[ids[i]] = i + 1;
                    newGames.AddRange(BuildBracketGames(t, ids, null));
                }
            }

            // everything checked - now write it
            foreach (var tp in await _db.TournamentPlayersTT.Where(p => p.TournamentId == tournamentId).ToListAsync())
            {
                tp.PlayoffSeed  = seedMap.TryGetValue(tp.PlayerId, out var seed) ? seed : null;
                tp.PlayoffBlock = blockMap.TryGetValue(tp.PlayerId, out var blk) ? blk : null;
            }
            _db.GamesTT.AddRange(newGames);
            await _db.SaveChangesAsync();
            return null;
        }

        private static List<GameTT> BuildBracketGames(TournamentTT t, List<int> seededByPosition, int? block) =>
            t.BracketType == TtPlayoffType.AllPlaces
                ? BuildAllPlacesGames(t.Id, seededByPosition, block)
                : BuildSingleEliminationGames(t.Id, seededByPosition, block);

        // ── group qualifiers ────────────────────────────────────────────────
        private sealed record Cand(int PlayerId, int? Group, int Cohort);

        private static double PerGame(int value, int played) => played == 0 ? 0 : (double)value / played;

        /// <summary>
        /// Players at group places fromPlace..toPlace-1 (0-based) of every group, best first: place by place, and inside
        /// one place ordered by points per game (so groups of different size are comparable), set difference,
        /// sets won and point difference per game. Cohort = the player's place inside this range.
        /// </summary>
        private static List<Cand> RankedQualifiers(Dictionary<int, List<TtStandingRow>> perGroup, int fromPlace, int toPlace)
        {
            var result = new List<Cand>();
            for (int place = fromPlace; place < toPlace; place++)
            {
                var tier = perGroup.Where(kv => kv.Value.Count > place)
                    .Select(kv => (Group: kv.Key, Row: kv.Value[place]))
                    .OrderByDescending(x => PerGame(x.Row.Points, x.Row.Played))
                    .ThenByDescending(x => PerGame(x.Row.SetDiff, x.Row.Played))
                    .ThenByDescending(x => PerGame(x.Row.SetsFor, x.Row.Played))
                    .ThenByDescending(x => PerGame(x.Row.PointDiff, x.Row.Played))
                    .ThenBy(x => x.Group);
                foreach (var x in tier) result.Add(new Cand(x.Row.Player.Id, x.Group, place - fromPlace));
            }
            return result;
        }

        /// <summary>
        /// Puts the ranked players on the bracket positions (index = seed - 1) so that nobody meets a player of his own
        /// group in round 1. Only players of the same cohort (e.g. all group runners-up) are swapped, so group winners stay
        /// the top seeds, runners-up the next ones, and so on. If no such draw exists the plain ranking is used.
        /// </summary>
        private static List<int> AssignSeeds(List<Cand> ranked, int size)
        {
            int n = ranked.Count;
            var natural = ranked.Select(c => c.PlayerId).ToList();
            if (ranked.All(c => c.Group == null)) return natural;

            var order = SeedOrder(size);
            var opponent = new int[n + 1];                    // round-1 opponent of seed s (0 = bye)
            for (int i = 0; i < size; i += 2)
            {
                int a = order[i], b = order[i + 1];
                if (a <= n && b <= n) { opponent[a] = b; opponent[b] = a; }
            }

            var assigned = new Cand?[n + 1];
            var used = new bool[n];
            int nodes = 0;

            bool Solve(int seed)
            {
                if (seed > n) return true;
                if (++nodes > 200_000) return false;
                int cohort = ranked[seed - 1].Cohort;
                for (int i = 0; i < n; i++)
                {
                    if (used[i] || ranked[i].Cohort != cohort) continue;
                    var cand = ranked[i];
                    int o = opponent[seed];
                    if (o != 0 && o < seed && cand.Group != null && assigned[o]!.Group == cand.Group) continue;
                    used[i] = true; assigned[seed] = cand;
                    if (Solve(seed + 1)) return true;
                    used[i] = false; assigned[seed] = null;
                }
                return false;
            }

            return Solve(1) ? Enumerable.Range(1, n).Select(sd => assigned[sd]!.PlayerId).ToList() : natural;
        }

        /// <summary>
        /// Placement blocks: places (b-1)*Q+1..b*Q of every group form block b. A block with 2, 4, 8, 16 ... players
        /// gets an all-places bracket (no byes); a block of any other size plays an additional group - the games its
        /// players already played against each other in the group stage count, only the missing games are created.
        /// </summary>
        private static string? BuildPlacementBlocks(TournamentTT t, Dictionary<int, List<TtStandingRow>> perGroup,
            List<GameTT> groupGames, Dictionary<int, int> seedMap, Dictionary<int, int> blockMap, List<GameTT> newGames)
        {
            int q = Math.Max(1, t.QualifiersPerGroup);
            int maxSize = perGroup.Count == 0 ? 0 : perGroup.Values.Max(r => r.Count);
            int blocks = (maxSize + q - 1) / q;
            if (blocks == 0) return "There are no players in the groups.";

            var played = groupGames.Where(g => g.Player1Id != null && g.Player2Id != null)
                                   .Select(g => PairKey(g.Player1Id!.Value, g.Player2Id!.Value)).ToHashSet();

            for (int b = 1; b <= blocks; b++)
            {
                var ranked = RankedQualifiers(perGroup, (b - 1) * q, Math.Min(b * q, maxSize));
                foreach (var c in ranked) blockMap[c.PlayerId] = b;
                int n = ranked.Count;
                if (n <= 1) continue;                                   // a single player: the place is simply his

                if ((n & (n - 1)) == 0)                                  // power of two: full bracket, no byes
                {
                    var ids = AssignSeeds(ranked, n);
                    for (int i = 0; i < ids.Count; i++) seedMap[ids[i]] = i + 1;
                    newGames.AddRange(BuildAllPlacesGames(t.Id, ids, b));
                }
                else                                                     // additional group
                {
                    var ids = ranked.Select(c => c.PlayerId).ToList();
                    foreach (var (round, a, c2) in RoundRobin(ids))
                    {
                        if (played.Contains(PairKey(a, c2))) continue;   // already played in the group stage
                        newGames.Add(new GameTT
                        {
                            TournamentId = t.Id, Stage = TtLocalLabels.StageBlockGroup, Block = b,
                            Round = round, Player1Id = a, Player2Id = c2
                        });
                    }
                }
            }
            return null;
        }

        private static int BracketSize(int n) { int size = 1; while (size < n) size *= 2; return size; }
        private static int Log2(int size) { int r = 0; while ((1 << r) < size) r++; return r; }

        private static List<GameTT> BuildSingleEliminationGames(int tournamentId, List<int> seeded, int? block = null)
        {
            int n = seeded.Count, size = BracketSize(n), rounds = Log2(size);
            var order = SeedOrder(size);

            var games = new Dictionary<(int Round, int Pos), GameTT>();
            for (int r = 1; r <= rounds; r++)
                for (int p = 0; p < (size >> r); p++)
                    games[(r, p)] = new GameTT { TournamentId = tournamentId, Stage = TtLocalLabels.StagePlayoff, Block = block, Round = r, Position = p };

            for (int p = 0; p < size / 2; p++)
            {
                int? a = order[2 * p]     <= n ? seeded[order[2 * p] - 1]     : null;
                int? b = order[2 * p + 1] <= n ? seeded[order[2 * p + 1] - 1] : null;
                if (a != null && b != null)
                {
                    games[(1, p)].Player1Id = a; games[(1, p)].Player2Id = b;
                }
                else
                {
                    // bye: the player goes straight to round 2, no round-1 game is created
                    int adv = (a ?? b)!.Value;
                    games.Remove((1, p));
                    var next = games[(2, p / 2)];
                    if (p % 2 == 0) next.Player1Id = adv; else next.Player2Id = adv;
                }
            }
            return games.Values.ToList();
        }

        /// <summary>
        /// All-places playoff. Round r has pools of size/2^(r-1) players; every pool plays one round, its winners
        /// form the upper half of the places, its losers the lower half. Missing players (when n isn't a power of
        /// two) are "byes" that always lose, so real players get walkovers and pass through automatically.
        /// </summary>
        private static List<GameTT> BuildAllPlacesGames(int tournamentId, List<int> seeded, int? block = null)
        {
            int n = seeded.Count, size = BracketSize(n), rounds = Log2(size);

            var games = new Dictionary<(int Round, int Pos), GameTT>();
            var phantom = new Dictionary<(int Round, int Pos), bool[]>();   // [0] = slot 1 is a bye, [1] = slot 2 is a bye
            for (int r = 1; r <= rounds; r++)
                for (int p = 0; p < size / 2; p++)
                {
                    games[(r, p)]   = new GameTT { TournamentId = tournamentId, Stage = TtLocalLabels.StagePlayoff, Block = block, Round = r, Position = p };
                    phantom[(r, p)] = new bool[2];
                }

            // round 1: games in bracket order (1-8, 4-5, 3-6, 2-7 ...), see SeedOrder
            var order = SeedOrder(size);
            for (int j = 0; j < size / 2; j++)
            {
                int s1 = order[2 * j], s2 = order[2 * j + 1];
                if (s1 <= n) games[(1, j)].Player1Id = seeded[s1 - 1]; else phantom[(1, j)][0] = true;
                if (s2 <= n) games[(1, j)].Player2Id = seeded[s2 - 1]; else phantom[(1, j)][1] = true;
            }

            // a bye never wins: the winner slot is a bye only if both are, the loser slot if either is
            for (int r = 1; r < rounds; r++)
                for (int p = 0; p < size / 2; p++)
                {
                    bool a = phantom[(r, p)][0], b = phantom[(r, p)][1];
                    var (w, l) = TtPlayoffGraph.Destinations(TtPlayoffType.AllPlaces, size, r, p);
                    phantom[(w!.Value.Round, w.Value.Pos)][w.Value.First ? 0 : 1] = a && b;
                    phantom[(l!.Value.Round, l.Value.Pos)][l.Value.First ? 0 : 1] = a || b;
                }

            foreach (var kv in games)
            {
                bool a = phantom[kv.Key][0], b = phantom[kv.Key][1];
                kv.Value.ByeSide = a && b ? 3 : a ? 1 : b ? 2 : null;
            }
            foreach (var key in games.Where(kv => kv.Value.ByeSide == 3).Select(kv => kv.Key).ToList())
                games.Remove(key);

            // real players that already stand in a bye game (round 1) walk on to the next round
            var map = new PlayoffMap(TtPlayoffType.AllPlaces, games.Values);
            foreach (var g in games.Values.OrderBy(x => x.Round).ThenBy(x => x.Position))
                if (g.IsBye && (g.Player1Id ?? g.Player2Id) != null)
                    map.Place(g, true, g.Player1Id ?? g.Player2Id);

            return games.Values.ToList();
        }

        // ══ Helpers (static, also used by the pages) ═════════════════════════
        private static (int, int) PairKey(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>Circle-method round robin. Returns (round, playerA, playerB).</summary>
        public static List<(int Round, int A, int B)> RoundRobin(List<int> ids)
        {
            var list = ids.Select(i => (int?)i).ToList();
            if (list.Count % 2 == 1) list.Add(null);
            int n = list.Count;
            var result = new List<(int, int, int)>();
            for (int r = 0; r < n - 1; r++)
            {
                for (int i = 0; i < n / 2; i++)
                {
                    var a = list[i]; var b = list[n - 1 - i];
                    if (a != null && b != null) result.Add((r + 1, a.Value, b.Value));
                }
                var last = list[n - 1];
                list.RemoveAt(n - 1);
                list.Insert(1, last);
            }
            return result;
        }

        /// <summary>
        /// Round-1 seed order, top to bottom of the bracket (two seeds per game): for 8 players
        /// 1-8, 4-5, 3-6, 2-7. Seed 1 is at the very top and seed 2 at the very bottom, 1 and 2 can only
        /// meet in the final, and neighbouring games always feed the same next-round game.
        /// This only matters in round 1 - from round 2 on the bracket is fixed.
        /// </summary>
        public static List<int> SeedOrder(int size)
        {
            var order = new List<int> { 1 };
            while (order.Count < size)
            {
                int total = order.Count * 2 + 1;
                var next = new List<int>();
                foreach (var s in order) { next.Add(s); next.Add(total - s); }
                order = next;
            }

            // mirror the lower half (keeping each game's two seeds together)
            if (size >= 4)
            {
                var upper = order.Take(size / 2);
                var lowerGames = order.Skip(size / 2).Chunk(2).Reverse().SelectMany(c => c);
                order = upper.Concat(lowerGames).ToList();
            }
            return order;
        }

        /// <summary>
        /// Standings of one group. Order: table points; then, for players level on points, the games played
        /// between exactly those players (head-to-head "mini table": points, set difference, sets won, point
        /// difference - re-applied to the players still level); then overall set difference, sets won, point
        /// difference; finally surname.
        /// </summary>
        public static List<TtStandingRow> BuildStandings(IEnumerable<PlayerTT> members, IEnumerable<GameTT> games)
        {
            var gameList = games.Where(g => g.IsFinished && g.Player1Id != null && g.Player2Id != null).ToList();
            var rows = Tally(members, gameList).Values.ToList();

            var result = new List<TtStandingRow>();
            foreach (var tier in rows.GroupBy(r => r.Points).OrderByDescending(t => t.Key))
                result.AddRange(ResolveTie(tier.ToList(), gameList));
            return result;
        }

        private static Dictionary<int, TtStandingRow> Tally(IEnumerable<PlayerTT> members, IEnumerable<GameTT> games)
        {
            var rows = members.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => new TtStandingRow { Player = g.First() });

            foreach (var g in games)
            {
                if (!rows.TryGetValue(g.Player1Id!.Value, out var a) || !rows.TryGetValue(g.Player2Id!.Value, out var b)) continue;
                int s1 = g.Player1Sets!.Value, s2 = g.Player2Sets!.Value;
                a.Played++; b.Played++;
                a.SetsFor += s1; a.SetsAgainst += s2;
                b.SetsFor += s2; b.SetsAgainst += s1;
                foreach (var set in g.Sets)
                {
                    a.PointsFor += set.Player1Points; a.PointsAgainst += set.Player2Points;
                    b.PointsFor += set.Player2Points; b.PointsAgainst += set.Player1Points;
                }
                if (s1 > s2) { a.Wins++; b.Losses++; } else { b.Wins++; a.Losses++; }
            }
            return rows;
        }

        /// <summary>Orders players who are level on table points (head-to-head first).</summary>
        private static List<TtStandingRow> ResolveTie(List<TtStandingRow> tied, List<GameTT> games)
        {
            if (tied.Count <= 1) return tied;

            // mini table: only the games between the tied players
            var ids  = tied.Select(r => r.Player.Id).ToHashSet();
            var mini = Tally(tied.Select(r => r.Player),
                             games.Where(g => ids.Contains(g.Player1Id!.Value) && ids.Contains(g.Player2Id!.Value))).Values;

            var tiers = mini.GroupBy(m => (m.Points, m.SetDiff, m.SetsFor, m.PointDiff))
                            .OrderByDescending(t => t.Key.Points).ThenByDescending(t => t.Key.SetDiff)
                            .ThenByDescending(t => t.Key.SetsFor).ThenByDescending(t => t.Key.PointDiff)
                            .ToList();

            if (tiers.Count == 1)
            {
                // head-to-head can't separate them: overall record, then alphabetical
                return tied.OrderByDescending(r => r.SetDiff).ThenByDescending(r => r.SetsFor)
                           .ThenByDescending(r => r.PointDiff).ThenBy(r => r.Player.Surname).ThenBy(r => r.Player.Name).ToList();
            }

            // head-to-head separated some of them: players still level are compared again among themselves only
            var result = new List<TtStandingRow>();
            foreach (var tier in tiers)
            {
                var members = tier.Select(m => tied.First(r => r.Player.Id == m.Player.Id)).ToList();
                result.AddRange(ResolveTie(members, games));
            }
            return result;
        }

        /// <summary>The blocks of a placement-blocks playoff, best places first.</summary>
        public static List<TtBlockInfo> DescribeBlocks(IEnumerable<TournamentPlayerTT> participants, IEnumerable<GameTT> games)
        {
            var gameList = games.ToList();
            var result = new List<TtBlockInfo>();
            int offset = 0;
            foreach (var blk in participants.Where(p => p.PlayoffBlock != null && p.Player != null)
                                            .GroupBy(p => p.PlayoffBlock!.Value).OrderBy(g => g.Key))
            {
                var players = blk.OrderBy(p => p.Id).Select(p => p.Player!).ToList();
                string kind = gameList.Any(g => g.Block == blk.Key && g.Stage == TtLocalLabels.StagePlayoff) ? "bracket"
                            : players.Count >= 2 ? "group" : "single";
                result.Add(new TtBlockInfo(blk.Key, offset + 1, offset + players.Count, kind, players));
                offset += players.Count;
            }
            return result;
        }

        /// <summary>
        /// Overall final standings of a placement-blocks tournament: every block decides its own range of places
        /// (bracket = final placements of the bracket, additional group = group table once all its games are played).
        /// Only places that are already decided are returned.
        /// </summary>
        public static List<(int Place, PlayerTT Player)> BuildFinalStandings(IReadOnlyList<TtBlockInfo> blocks, IEnumerable<GameTT> allGames)
        {
            var games = allGames.ToList();
            var result = new List<(int Place, PlayerTT Player)>();
            foreach (var b in blocks)
            {
                if (b.Kind == "single") { result.Add((b.PlaceFrom, b.Players[0])); continue; }

                if (b.Kind == "bracket")
                {
                    result.AddRange(BuildPlacements(games.Where(g => g.Block == b.Number && g.Stage == TtLocalLabels.StagePlayoff), b.PlaceFrom - 1));
                    continue;
                }

                var blockGames = games.Where(g => g.Block == b.Number && g.Stage == TtLocalLabels.StageBlockGroup).ToList();
                if (blockGames.Any(g => !g.IsFinished)) continue;     // group not finished yet
                var carried  = games.Where(g => g.Stage == TtLocalLabels.StageGroup);
                var standing = BuildStandings(b.Players, carried.Concat(blockGames));
                for (int i = 0; i < standing.Count; i++) result.Add((b.PlaceFrom + i, standing[i].Player));
            }
            return result.OrderBy(r => r.Place).ToList();
        }

        /// <summary>
        /// Final places of an all-places playoff (place 1 = winner). Only decided places are returned.
        /// The last round's pool k decides places 2k+1 and 2k+2.
        /// </summary>
        public static List<(int Place, PlayerTT Player)> BuildPlacements(IEnumerable<GameTT> playoffGames, int placeOffset = 0)
        {
            var list = playoffGames.Where(g => g.Round != null && g.Position != null).ToList();
            var result = new List<(int, PlayerTT)>();
            if (list.Count == 0) return result;

            int last = list.Max(g => g.Round!.Value);
            foreach (var g in list.Where(g => g.Round == last).OrderBy(g => g.Position))
            {
                int k = g.Position!.Value;
                if (g.IsBye)
                {
                    var real = g.Player1 ?? g.Player2;
                    if (real != null) result.Add((placeOffset + 2 * k + 1, real));
                }
                else if (g.IsFinished)
                {
                    bool p1Won = g.Player1Sets > g.Player2Sets;
                    var winner = p1Won ? g.Player1 : g.Player2;
                    var loser  = p1Won ? g.Player2 : g.Player1;
                    if (winner != null) result.Add((placeOffset + 2 * k + 1, winner));
                    if (loser  != null) result.Add((placeOffset + 2 * k + 2, loser));
                }
            }
            return result.OrderBy(r => r.Item1).ToList();
        }
    }

    /// <summary>Where the winner / loser of a playoff game goes next.</summary>
    public static class TtPlayoffGraph
    {
        public readonly record struct Slot(int Round, int Pos, bool First);

        /// <summary>
        /// Winner and loser destination of game (round, pos) in a bracket of 'size' (a power of two).
        /// Single elimination: only the winner moves on. All places: pool k of round r has G = size/2^r games;
        /// neighbouring games (2i, 2i+1) of a pool feed the same game of the next round: their winners play each
        /// other in the upper pool (2k), their losers in the lower pool (2k+1). The bracket is fixed after round 1.
        /// Null = no destination (last round / knocked out).
        /// </summary>
        public static (Slot? Winner, Slot? Loser) Destinations(TtPlayoffType type, int size, int round, int pos)
        {
            int rounds = 0; while ((1 << rounds) < size) rounds++;
            if (round >= rounds) return (null, null);

            if (type == TtPlayoffType.SingleElimination)
                return (new Slot(round + 1, pos / 2, pos % 2 == 0), null);

            int g = size >> round;            // games per pool in this round (even, >= 2 here)
            int pool = pos / g, j = pos % g;
            int next = g / 2;                 // games per pool in the next round
            int j2 = j / 2;
            bool first = j % 2 == 0;
            return (new Slot(round + 1, (2 * pool) * next + j2, first),
                    new Slot(round + 1, (2 * pool + 1) * next + j2, first));
        }
    }

    /// <summary>All playoff games of one tournament, with helpers to move players along the bracket.</summary>
    public class PlayoffMap
    {
        private readonly TtPlayoffType _type;
        private readonly Dictionary<(int, int), GameTT> _map;
        private readonly int _size;

        public PlayoffMap(TtPlayoffType type, IEnumerable<GameTT> games)
        {
            _type = type;
            _map  = games.Where(g => g.Round != null && g.Position != null).ToDictionary(g => (g.Round!.Value, g.Position!.Value));
            int rounds = _map.Count == 0 ? 0 : _map.Keys.Max(k => k.Item1);
            _size = 1 << rounds;
        }

        private GameTT? Dest(GameTT from, bool winner)
        {
            var (w, l) = TtPlayoffGraph.Destinations(_type, _size, from.Round!.Value, from.Position!.Value);
            var slot = winner ? w : l;
            return slot != null && _map.TryGetValue((slot.Value.Round, slot.Value.Pos), out var g) ? g : null;
        }

        /// <summary>Puts a player (or null = clear) into the winner/loser slot of 'from'. Bye games pass the player on.</summary>
        public void Place(GameTT from, bool asWinner, int? playerId)
        {
            var (w, l) = TtPlayoffGraph.Destinations(_type, _size, from.Round!.Value, from.Position!.Value);
            var slot = asWinner ? w : l;
            if (slot == null || !_map.TryGetValue((slot.Value.Round, slot.Value.Pos), out var dest)) return;

            if (slot.Value.First) dest.Player1Id = playerId; else dest.Player2Id = playerId;

            if (dest.IsBye) Place(dest, true, dest.Player1Id ?? dest.Player2Id);
        }

        /// <summary>Error text if a later, real game of this player was already played.</summary>
        public string? CheckDownstream(GameTT from)
        {
            foreach (bool winner in new[] { true, false })
            {
                var cur = Dest(from, winner);
                while (cur != null && cur.IsBye) cur = Dest(cur, true);
                if (cur != null && cur.IsFinished)
                    return "A later game of these players already has a result - clear that result first.";
            }
            return null;
        }
    }
}
