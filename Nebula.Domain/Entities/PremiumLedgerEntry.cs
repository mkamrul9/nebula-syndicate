// File: Nebula.Domain/Entities/PremiumLedgerEntry.cs
namespace Nebula.Domain.Entities
{
    public class PremiumLedgerEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PlayerId { get; set; }
        
        // Positive for income, Negative for expenses
        public int Amount { get; set; } 
        
        public TransactionType Type { get; set; }
        
        // e.g., Stripe PaymentIntentId, or the ItemId they purchased
        public string ReferenceId { get; set; } = string.Empty; 
        
        public DateTime TimestampUTC { get; set; } = DateTime.UtcNow;

        // Navigation Property
        public PlayerProfile? Player { get; set; }
    }

    public enum TransactionType
    {
        RealMoneyPurchase,
        QuestReward,
        AdminGrant,
        StorePurchase,
        GuildVaultDonation
    }
}
