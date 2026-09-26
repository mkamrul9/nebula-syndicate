// File: Nebula.Domain/Entities/AdminAuditLog.cs
namespace Nebula.Domain.Entities
{
    public class AdminAuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AdminId { get; set; }
        public string ActionType { get; set; } = string.Empty; // e.g., "BAN_PLAYER", "GRANT_COINS"
        public string TargetId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime TimestampUTC { get; set; } = DateTime.UtcNow;
    }
}
