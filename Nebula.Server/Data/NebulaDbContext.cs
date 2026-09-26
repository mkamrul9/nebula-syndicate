// File: Nebula.Server/Data/NebulaDbContext.cs
using Microsoft.EntityFrameworkCore;
using Nebula.Domain.Entities;

namespace Nebula.Server.Data
{
    public class NebulaDbContext : DbContext
    {
        public NebulaDbContext(DbContextOptions<NebulaDbContext> options) : base(options) { }

        public DbSet<PlayerProfile> Players { get; set; }
        public DbSet<MatchRecord> MatchRecords { get; set; }
        public DbSet<Guild> Guilds { get; set; }
        public DbSet<PlayerQuest> PlayerQuests { get; set; }
        public DbSet<PremiumLedgerEntry> PremiumLedgerEntries { get; set; }
        public DbSet<CosmeticItem> CosmeticItems { get; set; }
        public DbSet<PlayerCosmetic> PlayerCosmetics { get; set; }
        public DbSet<Season> Seasons { get; set; }
        public DbSet<SeasonTier> SeasonTiers { get; set; }
        public DbSet<PlayerSeasonProgress> PlayerSeasonProgress { get; set; }
        public DbSet<PlayerClaimedReward> PlayerClaimedRewards { get; set; }
        public DbSet<Tournament> Tournaments { get; set; }
        public DbSet<TournamentMatch> TournamentMatches { get; set; }
        public DbSet<AdminAuditLog> AdminAuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Ensure usernames are unique at the database level
            modelBuilder.Entity<PlayerProfile>()
                .HasIndex(p => p.Username)
                .IsUnique();

            modelBuilder.Entity<Guild>()
                .HasIndex(g => g.Name).IsUnique(); // Guild names must be unique
                
            modelBuilder.Entity<Guild>()
                .HasIndex(g => g.Tag).IsUnique(); // Tags must be unique

            modelBuilder.Entity<PlayerProfile>()
                .HasOne(p => p.Guild)
                .WithMany(g => g.Members)
                .HasForeignKey(p => p.GuildId)
                .OnDelete(DeleteBehavior.SetNull); // If guild is deleted, players just become guildless

            modelBuilder.Entity<PlayerCosmetic>()
                .HasKey(pc => new { pc.PlayerId, pc.CosmeticItemId });

            // Seed cosmetics
            modelBuilder.Entity<CosmeticItem>().HasData(
                new CosmeticItem { Id = 1, Name = "Neon Crimson", Description = "A red neon glowing drone skin.", Type = CosmeticType.DroneSkin, PriceInCoins = 500, AssetRef = "drone-neon-crimson" },
                new CosmeticItem { Id = 2, Name = "Gold Elite", Description = "Premium gold plated avatar.", Type = CosmeticType.Avatar, PriceInCoins = 1000, AssetRef = "avatar-gold-elite" }
            );

            // Configure Season entities
            modelBuilder.Entity<PlayerSeasonProgress>()
                .HasKey(p => new { p.PlayerId, p.SeasonId });

            modelBuilder.Entity<PlayerClaimedReward>()
                .HasKey(c => new { c.PlayerId, c.SeasonTierId });

            // Configure Tournament entities
            modelBuilder.Entity<TournamentMatch>()
                .HasOne(m => m.Tournament)
                .WithMany(t => t.Matches)
                .HasForeignKey(m => m.TournamentId);
        }
    }
}
