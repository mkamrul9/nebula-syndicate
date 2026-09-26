// File: Nebula.Domain/Entities/PlayerQuest.cs
namespace Nebula.Domain.Entities
{
    public class PlayerQuest
    {
        public Guid Id { get; set; }
        public Guid PlayerId { get; set; }
        
        public QuestType Type { get; set; }
        public string Description { get; set; } = string.Empty;
        
        public int TargetValue { get; set; }
        public int CurrentValue { get; set; }
        
        public int RewardCredits { get; set; }
        
        public bool IsCompleted => CurrentValue >= TargetValue;
        public bool IsClaimed { get; set; }
        
        public DateTime ExpirationDateUTC { get; set; }
    }

    public enum QuestType
    {
        PlayMatches,
        WinMatches,
        DeployDrones,
        UseSabotage
    }
}
