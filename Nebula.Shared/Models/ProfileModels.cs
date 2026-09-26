// File: Nebula.Shared/Models/ProfileModels.cs
namespace Nebula.Shared.Models
{
    public class PlayerStatsDto
    {
        public string Username { get; set; } = string.Empty;
        public int TotalMatchesPlayed { get; set; }
        public int TotalWins { get; set; }
        public decimal WinRate => TotalMatchesPlayed == 0 ? 0 : Math.Round((decimal)TotalWins / TotalMatchesPlayed * 100, 1);
        public int PremiumCredits { get; set; }
    }

    public class MatchHistoryItemDto
    {
        public string MatchId { get; set; } = string.Empty;
        public bool IsVictory { get; set; }
        public int DurationSeconds { get; set; }
        public DateTime EndedAt { get; set; }
    }
}
