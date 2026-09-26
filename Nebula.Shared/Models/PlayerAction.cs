// File: Nebula.Shared/Models/PlayerAction.cs
namespace Nebula.Shared.Models
{
    public enum ActionType
    {
        DeployDrone,
        SellResource,
        UseSabotage,
        BuildDefense
    }

    public enum SabotageType
    {
        EMP,
        MarketVirus
    }

    public enum DefenseType
    {
        Firewall
    }

    public readonly struct PlayerAction
    {
        public string PlayerId { get; init; }
        public ActionType Type { get; init; }
        
        // For Drone/Market actions
        public string TargetResource { get; init; }
        
        // For Sabotage actions
        public string TargetPlayerId { get; init; }
        public SabotageType? Sabotage { get; init; }

        // For Defense actions
        public DefenseType? Defense { get; init; }
    }
}
