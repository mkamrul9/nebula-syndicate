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

    public class PlayerAction
    {
        public string PlayerId { get; set; } = string.Empty;
        public ActionType Type { get; set; }
        
        // For Drone/Market actions
        public string TargetResource { get; set; } = string.Empty; 
        
        // For Sabotage actions
        public string TargetPlayerId { get; set; } = string.Empty; 
        public SabotageType? Sabotage { get; set; }

        // For Defense actions
        public DefenseType? Defense { get; set; }
    }
}
