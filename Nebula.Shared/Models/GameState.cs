using Nebula.Shared.Interfaces;

namespace Nebula.Shared.Models
{
    public class GameState
    {
        public string MatchId { get; set; } = string.Empty;
        public GameStatus Status { get; set; }
        public int CurrentTick { get; set; }

        // Set once when the match ends. Clients and persistence must use this, not a local credits check.
        public string? WinnerId { get; set; }

        public const decimal VictoryLine = 50000m;

        // Short market shock the whole match can see. Empty once the banner expires.
        public string MarketHeadline { get; set; } = string.Empty;
        public string MarketEventResource { get; set; } = string.Empty;
        public int MarketEventUntilTick { get; set; }

        // Match timer: tracks total elapsed ticks (10 per second)
        public int MatchDurationTicks { get; set; }

        // Global event: Power Surge — all drone yields boosted for 30s
        public int GlobalPowerSurgeUntilTick { get; set; }
        public bool IsGlobalPowerSurge { get; set; }

        // Guided first match: soft rival and in-arena coaching.
        public bool IsTutorial { get; set; }
        
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

        // This queue holds actions waiting for the next tick
        // We use System.Text.Json.Serialization.JsonIgnore
        // to prevent this from being serialized and sent to the client!
        [System.Text.Json.Serialization.JsonIgnore] 
        public System.Collections.Concurrent.ConcurrentQueue<PlayerAction> PendingActions { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore] // Don't send this over SignalR!
        public List<ReplayFrame> ReplayFrames { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public IAiBotController? ActiveBot { get; set; }
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
        
        // Replaces the simple boolean with a tick-based tracker
        public int EmpExpirationTick { get; set; }
        
        // Helper property so the Blazor UI easily knows if it should disable buttons
        public bool IsEmpMuted { get; set; } 

        public int FirewallCharges { get; set; }

        // Successful actions this match. Starting drones do not count.
        public int DronesDeployed { get; set; }
        public int SabotagesUsed { get; set; }

        // Timing skill: hot sales land the spike, cold sales dump into a glut.
        public int HotSales { get; set; }
        public int ColdSales { get; set; }

        // Highest credit milestone already announced this match (0, 10000, 25000, 40000).
        public int AnnouncedCreditMilestone { get; set; }

        // Drone Overcharge: doubles one resource's drone output for 200 ticks (20s).
        public int OverchargeIroniumUntilTick { get; set; }
        public int OverchargePlasmaUntilTick { get; set; }
        public bool IsIroniumOvercharged { get; set; }
        public bool IsPlasmaOvercharged { get; set; }

        // Market Virus: corrupts the victim's market display for 150 ticks (15s).
        public int MarketVirusUntilTick { get; set; }
        public bool HasMarketVirus { get; set; }

        // HOT-STREAK COMBO: consecutive hot sells grant a bonus multiplier (max x2.5)
        public int HotStreak { get; set; }           // current consecutive hot-sell count
        public decimal StreakMultiplier { get; set; } = 1.0m; // bonus applied on next sell
        public bool IsOnStreak { get; set; }

        // POWER SURGE: active player ability — boosts all drones 3x for 10s (costs ₡4000)
        public int PowerSurgeUntilTick { get; set; }
        public bool IsPowerSurgeActive { get; set; }

        // Dominance: tracks consecutive ticks in the lead
        public int DominanceTick { get; set; }
    }
}
