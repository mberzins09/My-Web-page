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
        }
    }
}
