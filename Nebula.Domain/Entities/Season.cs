using System;
using System.Collections.Generic;

namespace Nebula.Domain.Entities
{
    public class Season
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty; // e.g., "Season 1: Neon Uprising"
        public DateTime StartDateUTC { get; set; }
        public DateTime EndDateUTC { get; set; }
        
        // Navigation: A season has many tiers (Level 1 to 100)
        public ICollection<SeasonTier> Tiers { get; set; } = new List<SeasonTier>();
    }

    public class SeasonTier
    {
        public int Id { get; set; }
        public int SeasonId { get; set; }
        public int RequiredXP { get; set; } // Cumulative XP needed to reach this tier
        
        // The rewards (Linking to Phase 33 Cosmetics, or Premium Currency)
        public int? FreeCosmeticId { get; set; }
        public int? PremiumCosmeticId { get; set; }
        public int PremiumCurrencyReward { get; set; } // e.g., give 100 coins back on Premium track
    }

    // Tracks a player's XP and Premium status for a specific season
    public class PlayerSeasonProgress
    {
        public Guid PlayerId { get; set; }
        public int SeasonId { get; set; }
        public int TotalXP { get; set; }
        public bool HasPremiumPass { get; set; }
    }

    // Tracks exactly which rewards have been claimed to prevent double-dipping
    public class PlayerClaimedReward
    {
        public Guid PlayerId { get; set; }
        public int SeasonTierId { get; set; }
        public bool ClaimedFree { get; set; }
        public bool ClaimedPremium { get; set; }
    }
}
