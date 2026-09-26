// File: Nebula.Domain/Entities/Guild.cs
namespace Nebula.Domain.Entities
{
    public class Guild
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty; // e.g., [VOID], [APEX]
        public Guid LeaderId { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property for EF Core
        public ICollection<PlayerProfile> Members { get; set; } = new List<PlayerProfile>();
    }
}
