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
            string referenceId,
            bool manageTransaction = true)
        {
            if (amount == 0) return false;

            if (!manageTransaction)
                return await ApplyBalanceChangeAsync(playerId, amount, type, referenceId);

            // IsolationLevel.RepeatableRead ensures no other transaction can read or modify 
            // the player's balance while we are evaluating it.
            using var transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

            try
            {
                var success = await ApplyBalanceChangeAsync(playerId, amount, type, referenceId);
                if (!success)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                await transaction.CommitAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync();
                throw new Exception("Transaction collision detected. Please try again.");
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task<bool> ApplyBalanceChangeAsync(
            Guid playerId,
            int amount,
            TransactionType type,
            string referenceId)
        {
            var player = await _db.Players.FindAsync(playerId);
            if (player == null) return false;

            if (amount < 0 && player.PremiumCredits < Math.Abs(amount))
                return false;

            _db.Set<PremiumLedgerEntry>().Add(new PremiumLedgerEntry
            {
                PlayerId = playerId,
                Amount = amount,
                Type = type,
                ReferenceId = referenceId
            });

            player.PremiumCredits += amount;
            player.Version = Guid.NewGuid();

            await _db.SaveChangesAsync();
            return true;
        }
    }
}
