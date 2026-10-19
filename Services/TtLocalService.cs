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

            TournamentTT t;
            if (input.Id == 0) { t = new TournamentTT(); _db.TournamentsTT.Add(t); }
            else t = await _db.TournamentsTT.FindAsync(input.Id) ?? throw new InvalidOperationException("Tournament not found.");

            t.TournamentName     = input.TournamentName.Trim();
            t.TournamentDateTime = input.TournamentDateTime;
            t.Format             = input.Format;
            t.TournamentSeasonId = input.TournamentSeasonId;
            t.GroupCount         = input.GroupCount;
            t.QualifiersPerGroup = input.QualifiersPerGroup;
            t.SetsToWinGroup     = input.SetsToWinGroup;
            t.SetsToWinPlayoff   = input.SetsToWinPlayoff;
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
            if (g.Stage == TtLocalLabels.StagePlayoff) return "Playoff games can only be deleted together (delete the whole playoff).";
            if (await _db.GamesTT.AnyAsync(x => x.TournamentId == g.TournamentId && x.Stage == TtLocalLabels.StagePlayoff))
                return "Delete the playoff first - it was built from the group results.";
            _db.GamesTT.Remove(g);
            await _db.SaveChangesAsync();
            return null;
        }

        public async Task<string?> DeleteStageGamesAsync(int tournamentId, string stage)
        {
            if (stage == TtLocalLabels.StageGroup &&
                await _db.GamesTT.AnyAsync(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StagePlayoff))
                return "Delete the playoff first - it was built from the group results.";

            var games = await _db.GamesTT.Where(g => g.TournamentId == tournamentId && g.Stage == stage).ToListAsync();
            _db.GamesTT.RemoveRange(games);   // set points go with them (cascade)
            await _db.SaveChangesAsync();
            return null;
        }

        // ══ Results ══════════════════════════════════════════════════════════
        /// <summary>
        /// Saves a game result. If any set points are given, the sets won are calculated from them.
        /// Otherwise manualP1/manualP2 (sets won) are used and no set points are stored.
        /// A playoff winner is moved into the next round automatically.
        /// </summary>
        public async Task<string?> SaveResultAsync(int gameId, int? manualP1, int? manualP2,
            IReadOnlyList<(int? P1, int? P2)> setPoints)
        {
            var g = await _db.GamesTT.Include(x => x.Sets).Include(x => x.Tournament)
                                     .FirstOrDefaultAsync(x => x.Id == gameId);
            if (g == null) return "Game not found.";
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

            // 3) playoff: find the next-round slot, refuse if it already has a result
            var (next, firstSlot, nextErr) = await FindNextPlayoffGameAsync(g);
            if (nextErr != null) return nextErr;

            // 4) save
            g.Player1Sets = s1; g.Player2Sets = s2;
            _db.GameSetsTT.RemoveRange(g.Sets.ToList());
            g.Sets.Clear();
            for (int i = 0; i < rows.Count; i++)
                g.Sets.Add(new GameSetTT { SetNumber = i + 1, Player1Points = rows[i].P1, Player2Points = rows[i].P2 });

            if (next != null)
            {
                int winner = s1 > s2 ? g.Player1Id!.Value : g.Player2Id!.Value;
                if (firstSlot) next.Player1Id = winner; else next.Player2Id = winner;
            }
            await _db.SaveChangesAsync();
            return null;
        }

        /// <summary>Marks a game as not played again (removes sets and set points).</summary>
        public async Task<string?> ClearResultAsync(int gameId)
        {
            var g = await _db.GamesTT.Include(x => x.Sets).FirstOrDefaultAsync(x => x.Id == gameId);
            if (g == null) return null;

            var (next, firstSlot, nextErr) = await FindNextPlayoffGameAsync(g);
            if (nextErr != null) return nextErr;

            g.Player1Sets = null; g.Player2Sets = null;
            _db.GameSetsTT.RemoveRange(g.Sets.ToList());
            g.Sets.Clear();
            if (next != null) { if (firstSlot) next.Player1Id = null; else next.Player2Id = null; }
            await _db.SaveChangesAsync();
            return null;
        }

        private async Task<(GameTT? Next, bool FirstSlot, string? Error)> FindNextPlayoffGameAsync(GameTT g)
        {
            if (g.Stage != TtLocalLabels.StagePlayoff || g.Round == null || g.Position == null) return (null, false, null);
            int nextRound = g.Round.Value + 1, nextPos = g.Position.Value / 2;
            var next = await _db.GamesTT.FirstOrDefaultAsync(x => x.TournamentId == g.TournamentId &&
                x.Stage == TtLocalLabels.StagePlayoff && x.Round == nextRound && x.Position == nextPos);
            if (next == null) return (null, false, null);   // this was the final
            if (next.IsFinished) return (null, false, "The next round game already has a result - clear that result first.");
            return (next, g.Position.Value % 2 == 0, null);
        }

        // ══ Playoff bracket ══════════════════════════════════════════════════
        /// <summary>
        /// Builds the single-elimination bracket. With a group stage, the top QualifiersPerGroup of every
        /// group qualify (all group winners are seeded first, then all runners-up, ...).
        /// Playoff-only tournaments are seeded by the participants' Seed numbers.
        /// If the player count isn't a power of two, the best seeds get a bye in round 1.
        /// </summary>
        public async Task<string?> GeneratePlayoffAsync(int tournamentId)
        {
            var t = await _db.TournamentsTT.FindAsync(tournamentId);
            if (t == null) return "Tournament not found.";
            if (!t.HasPlayoff) return "This tournament has no playoff.";
            if (await _db.GamesTT.AnyAsync(g => g.TournamentId == tournamentId && g.Stage == TtLocalLabels.StagePlayoff))
                return "The playoff already exists. Delete it first if you want to generate it again.";

            var parts = await _db.TournamentPlayersTT.AsNoTracking().Include(p => p.Player)
                                  .Where(p => p.TournamentId == tournamentId).ToListAsync();
            List<int> seeded;

            if (t.Format == TtTournamentFormat.PlayoffOnly)
            {
                seeded = parts.OrderBy(p => p.Seed ?? int.MaxValue).ThenBy(p => p.Id).Select(p => p.PlayerId).ToList();
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
                seeded = new List<int>();
                for (int place = 0; place < t.QualifiersPerGroup; place++)
                {
                    var tier = perGroup.Values.Where(rows => rows.Count > place).Select(rows => rows[place])
                        .OrderByDescending(r => r.Points).ThenByDescending(r => r.SetDiff).ThenByDescending(r => r.SetsFor)
                        .Select(r => r.Player.Id);
                    seeded.AddRange(tier);
                }
            }

            int n = seeded.Count;
            if (n < 2) return "At least 2 players are needed for a playoff.";

            int size = 1, rounds = 0;
            while (size < n) { size *= 2; rounds++; }
            var order = SeedOrder(size);

            var games = new Dictionary<(int Round, int Pos), GameTT>();
            for (int r = 1; r <= rounds; r++)
                for (int p = 0; p < (size >> r); p++)
                    games[(r, p)] = new GameTT { TournamentId = tournamentId, Stage = TtLocalLabels.StagePlayoff, Round = r, Position = p };

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

            _db.GamesTT.AddRange(games.Values);
            await _db.SaveChangesAsync();
            return null;
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

        /// <summary>Standard bracket seeding: 1 plays size, 2 plays size-1 ... and 1 and 2 can only meet in the final.</summary>
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
            return order;
        }

        /// <summary>Standings of one group. Order: points, set difference, sets won, point difference.</summary>
        public static List<TtStandingRow> BuildStandings(IEnumerable<PlayerTT> members, IEnumerable<GameTT> games)
        {
            var rows = members.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => new TtStandingRow { Player = g.First() });

            foreach (var g in games.Where(g => g.IsFinished && g.Player1Id != null && g.Player2Id != null))
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

            return rows.Values.OrderByDescending(r => r.Points).ThenByDescending(r => r.SetDiff)
                              .ThenByDescending(r => r.SetsFor).ThenByDescending(r => r.PointDiff)
                              .ThenBy(r => r.Player.Surname).ThenBy(r => r.Player.Name).ToList();
        }
    }
}
