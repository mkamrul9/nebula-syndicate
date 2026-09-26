// File: Nebula.Domain/Entities/CosmeticItem.cs
namespace Nebula.Domain.Entities
{
    public class CosmeticItem
    {
        public int Id { get; set; } // Static ID seeded in DbContext
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public CosmeticType Type { get; set; }
        public int PriceInCoins { get; set; }
        public string AssetRef { get; set; } = string.Empty; // CSS class or image URL
    }

    public enum CosmeticType
    {
        Avatar,
        DroneSkin,
        UiTheme
    }

    // Junction table for Player Inventory
    public class PlayerCosmetic
    {
        public Guid PlayerId { get; set; }
        public int CosmeticItemId { get; set; }
        
        public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
        public bool IsEquipped { get; set; }

        public PlayerProfile? Player { get; set; }
        public CosmeticItem? Item { get; set; }
    }
}
