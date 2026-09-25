// File: Nebula.Domain/Entities/MatchRecord.cs
namespace Nebula.Domain.Entities
{
    public class MatchRecord
    {
        public Guid Id { get; set; }
        public Guid WinnerId { get; set; }
        public int DurationInSeconds { get; set; }
        public DateTime EndedAt { get; set; } = DateTime.UtcNow;
        
        // Store the final state as a JSON document for post-match analytics
        public string FinalStateJson { get; set; } = string.Empty; 
    }
}
