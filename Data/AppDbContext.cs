using MartinsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace MartinsWeb.Data
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        public DbSet<User>             Users            => Set<User>();
        public DbSet<Game>             Games            => Set<Game>();
        public DbSet<Prediction>       Predictions      => Set<Prediction>();
        public DbSet<TournamentGroup>  TournamentGroups => Set<TournamentGroup>();
        public DbSet<GroupTeam>        GroupTeams       => Set<GroupTeam>();
        public DbSet<Tournament>       Tournaments      => Set<Tournament>();
        public DbSet<UserGroup>        UserGroups       => Set<UserGroup>();
        public DbSet<UserGroupMember>  UserGroupMembers => Set<UserGroupMember>();
        public DbSet<PredictionsHistory> PredictionsHistories { get; set; } = null!;
        public DbSet<PredictionsHistoryEntry> PredictionsHistoryEntries { get; set; } = null!;
        public DbSet<LgtfEntry> LgtfEntries { get; set; } = null!;
        public DbSet<ApiSportsConfig> ApiSportsConfigs { get; set; }
        public DbSet<ApiTeamMapping> ApiTeamMappings { get; set; }
        public DbSet<ApiSyncLog> ApiSyncLogs { get; set; }

        // Komandu cempis
        public DbSet<SeptemberPlayer> SeptemberPlayers { get; set; } = null!;
        public DbSet<TeamEntry> TeamEntries { get; set; } = null!;
        public DbSet<TeamPlayerEntry> TeamPlayerEntries { get; set; } = null!;

        // Valstis (countries + per-sport-year world rankings)
        public DbSet<Country> Countries { get; set; } = null!;
        public DbSet<CountryRanking> CountryRankings { get; set; } = null!;

        // Local table tennis tournaments
        public DbSet<PlayerTT> PlayersTT { get; set; } = null!;
        public DbSet<TournamentSeason> TournamentSeasons { get; set; } = null!;
        public DbSet<TournamentTT> TournamentsTT { get; set; } = null!;
        public DbSet<TournamentPlayerTT> TournamentPlayersTT { get; set; } = null!;
        public DbSet<GameTT> GamesTT { get; set; } = null!;
        public DbSet<GameSetTT> GameSetsTT { get; set; } = null!;


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── Prediction ────────────────────────────────────────────────────
            modelBuilder.Entity<Prediction>(e =>
            {
                e.HasIndex(p => new { p.UserId, p.GameId }).IsUnique();

                e.HasOne(p => p.User)
                    .WithMany(u => u.Predictions)
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(p => p.Game)
                    .WithMany(g => g.Predictions)
                    .HasForeignKey(p => p.GameId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── GroupTeam → TournamentGroup (cascade on group delete) ─────────
            modelBuilder.Entity<GroupTeam>(e =>
            {
                e.HasOne(gt => gt.Group)
                    .WithMany(g => g.Teams)
                    .HasForeignKey(gt => gt.GroupId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Game FKs ──────────────────────────────────────────────────────
            modelBuilder.Entity<Game>(e =>
            {
                e.HasOne(g => g.TournamentGroup)
                    .WithMany(tg => tg.Games)
                    .HasForeignKey(g => g.GroupId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(g => g.Tournament)
                    .WithMany(t => t.Games)
                    .HasForeignKey(g => g.TournamentId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // ── TournamentGroup → Tournament ──────────────────────────────────
            modelBuilder.Entity<TournamentGroup>(e =>
            {
                e.HasOne(tg => tg.Tournament)
                    .WithMany(t => t.Groups)
                    .HasForeignKey(tg => tg.TournamentId)
                    .IsRequired(false)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // ── UserGroup → Tournament ────────────────────────────────────────
            modelBuilder.Entity<UserGroup>(e =>
            {
                e.HasOne(g => g.Tournament)
                    .WithMany()
                    .HasForeignKey(g => g.TournamentId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── UserGroupMember ───────────────────────────────────────────────
            modelBuilder.Entity<UserGroupMember>(e =>
            {
                e.HasIndex(m => new { m.UserGroupId, m.UserId }).IsUnique();

                e.HasOne(m => m.UserGroup)
                    .WithMany(g => g.Members)
                    .HasForeignKey(m => m.UserGroupId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(m => m.User)
                    .WithMany()
                    .HasForeignKey(m => m.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Komandu cempis ───────────────────────────────────────────────
            modelBuilder.Entity<TeamPlayerEntry>(e =>
            {
                e.HasOne(p => p.Team)
                 .WithMany(t => t.Players)
                 .HasForeignKey(p => p.TeamEntryId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Valstis ─────────────────────────────────────────────────────
            modelBuilder.Entity<CountryRanking>(e =>
            {
                e.HasOne(r => r.Country)
                 .WithMany(c => c.Rankings)
                 .HasForeignKey(r => r.CountryId)
                 .OnDelete(DeleteBehavior.Cascade);

                // One ranking per country/sport/year
                e.HasIndex(r => new { r.CountryId, r.Sport, r.Year }).IsUnique();
            });

            // ── Local table tennis ───────────────────────────────────────────
            modelBuilder.Entity<PlayerTT>(e =>
            {
                e.ToTable("PlayersTT");
                e.Property(p => p.UserId).HasColumnName("User_Id");
                e.HasIndex(p => p.UserId).IsUnique();
                e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<TournamentSeason>(e => e.ToTable("TournamentSeason"));

            modelBuilder.Entity<TournamentTT>(e =>
            {
                e.ToTable("TournamentsTT");
                e.Property(t => t.TournamentSeasonId).HasColumnName("TournamentSeason_Id");
                e.Property(t => t.Format).HasConversion<string>();
                e.HasOne(t => t.TournamentSeason).WithMany(s => s.Tournaments)
                 .HasForeignKey(t => t.TournamentSeasonId).IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<TournamentPlayerTT>(e =>
            {
                e.ToTable("TournamentPlayersTT");
                e.HasIndex(p => new { p.TournamentId, p.PlayerId }).IsUnique();
                e.HasOne(p => p.Tournament).WithMany(t => t.Participants)
                 .HasForeignKey(p => p.TournamentId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(p => p.Player).WithMany()
                 .HasForeignKey(p => p.PlayerId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GameTT>(e =>
            {
                e.ToTable("GamesTT");
                e.Property(g => g.Player1Id).HasColumnName("player1_id");
                e.Property(g => g.Player2Id).HasColumnName("player2_id");
                e.Property(g => g.Player1Sets).HasColumnName("player1_sets");
                e.Property(g => g.Player2Sets).HasColumnName("player2_sets");
                e.Property(g => g.TournamentId).HasColumnName("tournament_id");
                e.HasOne(g => g.Tournament).WithMany(t => t.Games)
                 .HasForeignKey(g => g.TournamentId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(g => g.Player1).WithMany()
                 .HasForeignKey(g => g.Player1Id).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(g => g.Player2).WithMany()
                 .HasForeignKey(g => g.Player2Id).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GameSetTT>(e =>
            {
                e.ToTable("GameSetsTT");
                e.HasIndex(s => new { s.GameId, s.SetNumber }).IsUnique();
                e.HasOne(s => s.Game).WithMany(g => g.Sets)
                 .HasForeignKey(s => s.GameId).OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
