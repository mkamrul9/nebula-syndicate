// File: Nebula.Shared/Models/GameState.cs
namespace Nebula.Shared.Models
{
    public class GameState
    {
        public string MatchId { get; set; } = string.Empty;
        public GameStatus Status { get; set; }
        public int CurrentTick { get; set; }
        
        // Dictionary mapping PlayerId to their current resources/status
        public Dictionary<string, PlayerState> Players { get; set; } = new();
        
        // Global market prices for resources
        public Dictionary<string, decimal> MarketPrices { get; set; } = new();
    }

    public enum GameStatus
    {
        WaitingForPlayers,
        InProgress,
        Finished
    }

    public class PlayerState
    {
        public string PlayerName { get; set; } = string.Empty;
        public int Credits { get; set; }
        public int Ironium { get; set; } // Example Resource
        public int ActiveDrones { get; set; }
        public bool IsEmpMuted { get; set; } // Status effect
    }
}
