// File: Nebula.Domain/Entities/PlayerProfile.cs
namespace Nebula.Domain.Entities
{
    public class PlayerProfile
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        
        // Persistent global currency, NOT in-match resources
        public int PremiumCredits { get; set; } 
        public int TotalMatchesPlayed { get; set; }
        public int TotalWins { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
