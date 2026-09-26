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
        
        // Tracks the current supply/demand momentum
        // Negative = Oversupplied (Price dropping)
        // Positive = High Demand (Price rising)
        public Dictionary<string, decimal> MarketPressures { get; set; } = new();

        // The "Default" price the market wants to return to when left alone
        public Dictionary<string, decimal> BasePrices { get; set; } = new();
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
        public bool IsConnected { get; set; } = true;
        public DateTime? DisconnectedAt { get; set; } 
        
        // Use decimal for exact currency arithmetic (preventing floating point drift with money)
        public decimal Credits { get; set; } 
        
        // Use double for continuous physical resources
        public double Ironium { get; set; }
        public double Plasma { get; set; }
        
        public int ActiveIroniumDrones { get; set; }
        public int ActivePlasmaDrones { get; set; }
        
        public bool IsEmpMuted { get; set; } 
    }
}
