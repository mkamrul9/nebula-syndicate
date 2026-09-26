// File: Nebula.Shared/Models/PlayerAction.cs
namespace Nebula.Shared.Models
{
    public class PlayerAction
    {
        public string PlayerId { get; set; } = string.Empty;
        public ActionType Type { get; set; }
        public string TargetResource { get; set; } = string.Empty; // "Ironium" or "Plasma"
    }

    public enum ActionType
    {
        DeployDrone,
        SellResource,
        UseSabotage
    }
}
