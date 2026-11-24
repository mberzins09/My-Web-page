using System.ComponentModel.DataAnnotations.Schema;

namespace MartinsWeb.Models
{
    /// <summary>How a local table tennis tournament is played.</summary>
    public enum TtTournamentFormat
    {
        GroupStage,           // only a group stage (round robin)
        GroupStageAndPlayoff, // group stage, then a playoff
        PlayoffOnly           // straight to the playoff
    }

    /// <summary>How the playoff is played.</summary>
    public enum TtPlayoffType
    {
        SingleElimination, // knockout: the loser is out
        AllPlaces,         // every place is played out: winners/losers split into "places 1-8" / "places 9-16" ... down to single games
        PlacementBlocks    // groups + playoff only: group places are split into blocks (places 1-2 of every group -> bracket for the top places,
                           // places 3-4 -> bracket for the next places ...); a block that can't be a full bracket is played as an additional group
    }

    public static class TtLocalLabels
    {
        public const string StageGroup   = "Group";
        public const string StagePlayoff = "Playoff";

        /// <summary>Games of an additional group (placement blocks that are not a full bracket).</summary>
        public const string StageBlockGroup = "BlockGroup";

        public static string Format(TtTournamentFormat f) => f switch
        {
            TtTournamentFormat.GroupStage           => "Group stage",
            TtTournamentFormat.GroupStageAndPlayoff => "Group stage + playoff",
            TtTournamentFormat.PlayoffOnly          => "Playoff only",
            _ => f.ToString()
        };

        /// <summary>1 -> "A", 2 -> "B" ...</summary>
        public static string GroupName(int number) =>
            number >= 1 && number <= 26 ? ((char)('A' + number - 1)).ToString() : number.ToString();

        public static string PlayoffType(TtPlayoffType t) => t switch
        {
            TtPlayoffType.SingleElimination => "Single elimination (knockout)",
            TtPlayoffType.AllPlaces         => "All places played (winners / losers brackets)",
            TtPlayoffType.PlacementBlocks   => "All places by group place (a bracket or group for every block of places)",
            _ => t.ToString()
        };

        /// <summary>"Places 1–8" for a game of an all-places playoff. size = bracket size (power of two).</summary>
        public static string PoolLabel(int size, int round, int position, int placeOffset = 0)
        {
            int gamesPerPool = size >> round;
            int poolSize     = size >> (round - 1);
            int pool         = position / gamesPerPool;
            int start        = placeOffset + pool * poolSize + 1;
            return $"Places {start}–{start + poolSize - 1}";
        }

        /// <summary>"Name Surname (3)" - the seed is only added when the player has one in the playoff.</summary>
        public static string WithSeed(PlayerTT? player, IReadOnlyDictionary<int, int>? seeds, string empty = "TBD")
        {
            if (player == null) return empty;
            return seeds != null && seeds.TryGetValue(player.Id, out var seed)
                ? $"{player.FullName} ({seed})"
                : player.FullName;
        }

        public static string SetsToWin(int toWin) =>
            toWin == 1 ? "1 set (single set)" : $"first to {toWin} sets (best of {toWin * 2 - 1})";

        /// <summary>Round 'round' of a bracket that has 'totalRounds' rounds.</summary>
        public static string PlayoffRoundName(int round, int totalRounds)
        {
            int fromEnd = totalRounds - round;
            return fromEnd switch
            {
                0 => "Final",
                1 => "Semi-final",
                2 => "Quarter-final",
                _ => $"Round of {1 << (fromEnd + 1)}"
            };
        }
    }

    /// <summary>A local player. UserId optionally links the player to a website account.</summary>
    public class PlayerTT
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";

        public int? UserId { get; set; }
        public User? User { get; set; }

        [NotMapped] public string FullName => $"{Name} {Surname}".Trim();
    }

    /// <summary>Optional grouping of tournaments (e.g. "Autumn 2026").</summary>
    public class TournamentSeason
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ICollection<TournamentTT> Tournaments { get; set; } = new List<TournamentTT>();
    }

    public class TournamentTT
    {
        public int Id { get; set; }
        public string TournamentName { get; set; } = "";
        public DateTime TournamentDateTime { get; set; } = DateTime.Today.AddHours(18);
        public TtTournamentFormat Format { get; set; } = TtTournamentFormat.GroupStage;

        public int? TournamentSeasonId { get; set; }
        public TournamentSeason? TournamentSeason { get; set; }

        /// <summary>Number of groups (only used when the format has a group stage).</summary>
        public int GroupCount { get; set; } = 1;

        /// <summary>How many players per group go on to the playoff (GroupStageAndPlayoff only).</summary>
        public int QualifiersPerGroup { get; set; } = 2;

        /// <summary>Sets a player must win to win a group game (1 = single set showdown).</summary>
        public int SetsToWinGroup { get; set; } = 3;

        /// <summary>Sets a player must win to win a playoff game.</summary>
        public int SetsToWinPlayoff { get; set; } = 3;

        /// <summary>Knockout or "all places" playoff (only used when the format has a playoff).</summary>
        public TtPlayoffType PlayoffType { get; set; } = TtPlayoffType.SingleElimination;

        /// <summary>The bracket system used inside a bracket: placement blocks always use "all places" brackets.</summary>
        [NotMapped] public TtPlayoffType BracketType =>
            PlayoffType == TtPlayoffType.PlacementBlocks ? TtPlayoffType.AllPlaces : PlayoffType;

        [NotMapped] public bool HasGroups  => Format != TtTournamentFormat.PlayoffOnly;
        [NotMapped] public bool HasPlayoff => Format != TtTournamentFormat.GroupStage;

        public ICollection<TournamentPlayerTT> Participants { get; set; } = new List<TournamentPlayerTT>();
        public ICollection<GameTT> Games { get; set; } = new List<GameTT>();
    }

    /// <summary>A player taking part in a tournament, with their group and/or seed.</summary>
    public class TournamentPlayerTT
    {
        public int Id { get; set; }

        public int TournamentId { get; set; }
        public TournamentTT? Tournament { get; set; }

        public int PlayerId { get; set; }
        public PlayerTT? Player { get; set; }

        /// <summary>1-based group number; null when not assigned / no group stage.</summary>
        public int? GroupNumber { get; set; }

        /// <summary>Seed for a playoff-only bracket (1 = best).</summary>
        public int? Seed { get; set; }

        /// <summary>Seed the player got in the generated playoff bracket (null = not in the playoff).</summary>
        public int? PlayoffSeed { get; set; }

        /// <summary>Placement-blocks playoff: the block (1 = top places) the player was put into.</summary>
        public int? PlayoffBlock { get; set; }
    }

    public class GameTT
    {
        public int Id { get; set; }

        public int TournamentId { get; set; }
        public TournamentTT? Tournament { get; set; }

        /// <summary>"Group" or "Playoff" (see TtLocalLabels).</summary>
        public string Stage { get; set; } = TtLocalLabels.StageGroup;

        /// <summary>Group games: which group (1-based).</summary>
        public int? GroupNumber { get; set; }

        /// <summary>Group games: round-robin round. Playoff games: bracket round (1 = first round).</summary>
        public int? Round { get; set; }

        /// <summary>Playoff games: position inside the round (0 = top of the bracket).</summary>
        public int? Position { get; set; }

        /// <summary>Placement-blocks playoff: which block this playoff / additional-group game belongs to.</summary>
        public int? Block { get; set; }

        // Players are nullable: a later playoff round is created before its players are known.
        public int? Player1Id { get; set; }
        public PlayerTT? Player1 { get; set; }
        public int? Player2Id { get; set; }
        public PlayerTT? Player2 { get; set; }

        /// <summary>Sets won. Both null = game not played yet.</summary>
        public int? Player1Sets { get; set; }
        public int? Player2Sets { get; set; }

        /// <summary>Optional points of each set (12:10, 11:3, ...).</summary>
        public ICollection<GameSetTT> Sets { get; set; } = new List<GameSetTT>();

        /// <summary>
        /// All-places playoff with a number of players that isn't a power of two: 1 = player 1 is a bye (no opponent),
        /// 2 = player 2 is a bye. The real player passes through automatically. (3 = both are byes; such games are never stored.)
        /// </summary>
        public int? ByeSide { get; set; }

        [NotMapped] public bool IsBye => ByeSide == 1 || ByeSide == 2;
        [NotMapped] public bool IsFinished => Player1Sets.HasValue && Player2Sets.HasValue;

        /// <summary>The winner - for a bye game the real player, once he/she has arrived.</summary>
        [NotMapped] public int? WinnerId => IsBye ? (Player1Id ?? Player2Id)
            : !IsFinished ? null : Player1Sets > Player2Sets ? Player1Id : Player2Id;
    }

    /// <summary>The points of one set of a game.</summary>
    public class GameSetTT
    {
        public int Id { get; set; }

        public int GameId { get; set; }
        public GameTT? Game { get; set; }

        public int SetNumber { get; set; }
        public int Player1Points { get; set; }
        public int Player2Points { get; set; }
    }

    /// <summary>A block of places in a placement-blocks playoff. Kind: "bracket", "group" or "single" (one player).</summary>
    public record TtBlockInfo(int Number, int PlaceFrom, int PlaceTo, string Kind, List<PlayerTT> Players);

    /// <summary>One position of a drawn bracket: a game, a "bye" placeholder (Game null + ByeName) or an empty slot.</summary>
    public record TtBracketSlot(GameTT? Game, string? ByeName);

    /// <summary>One row of a computed group standings table.</summary>
    public class TtStandingRow
    {
        public PlayerTT Player { get; set; } = null!;
        public int Played { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int SetsFor { get; set; }
        public int SetsAgainst { get; set; }
        public int PointsFor { get; set; }      // only counts sets where set points were recorded
        public int PointsAgainst { get; set; }

        public int SetDiff => SetsFor - SetsAgainst;
        public int PointDiff => PointsFor - PointsAgainst;

        /// <summary>Table points: win = 2, played-and-lost = 1 (same as the Prognozes table tennis groups).</summary>
        public int Points => Wins * 2 + Losses;
    }
}
