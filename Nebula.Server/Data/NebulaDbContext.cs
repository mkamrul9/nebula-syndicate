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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Ensure usernames are unique at the database level
            modelBuilder.Entity<PlayerProfile>()
                .HasIndex(p => p.Username)
                .IsUnique();
        }
    }
}
