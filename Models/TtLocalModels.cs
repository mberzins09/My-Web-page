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

    public static class TtLocalLabels
    {
        public const string StageGroup   = "Group";
        public const string StagePlayoff = "Playoff";

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

        [NotMapped] public bool IsFinished => Player1Sets.HasValue && Player2Sets.HasValue;
        [NotMapped] public int? WinnerId => !IsFinished ? null : Player1Sets > Player2Sets ? Player1Id : Player2Id;
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
