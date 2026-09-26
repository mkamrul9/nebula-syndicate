// File: Nebula.Server/Services/PremiumCurrencyService.cs
using Microsoft.EntityFrameworkCore;
using Nebula.Server.Data;
using Nebula.Domain.Entities;

namespace Nebula.Server.Services
{
    public class PremiumCurrencyService
    {
        private readonly NebulaDbContext _db;

        public PremiumCurrencyService(NebulaDbContext db)
        {
            _db = db;
        }

        public async Task<bool> AdjustBalanceAsync(
            Guid playerId, 
            int amount, 
            TransactionType type, 
            string referenceId)
        {
            if (amount == 0) return false;

            // IsolationLevel.RepeatableRead ensures no other transaction can read or modify 
            // the player's balance while we are evaluating it.
            using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

            try
            {
                var player = await _db.Players.FindAsync(playerId);
                if (player == null) return false;

                // Prevent going into debt on negative transactions (expenses)
                if (amount < 0 && player.PremiumCredits < Math.Abs(amount))
                {
                    return false; // Insufficient funds
                }

                // 1. Insert the immutable ledger entry
                var ledgerEntry = new PremiumLedgerEntry
                {
                    PlayerId = playerId,
                    Amount = amount,
                    Type = type,
                    ReferenceId = referenceId
                };
                _db.Set<PremiumLedgerEntry>().Add(ledgerEntry);

                // 2. Update the cached balance on the profile
                player.PremiumCredits += amount;
                
                // Force version update to catch any out-of-band concurrency issues
                player.Version = Guid.NewGuid(); 

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync();
                // In a production app, you might want to retry this automatically
                throw new Exception("Transaction collision detected. Please try again.");
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}
