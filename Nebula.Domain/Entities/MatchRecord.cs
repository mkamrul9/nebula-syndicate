// File: Nebula.Domain/Entities/MatchRecord.cs
namespace Nebula.Domain.Entities
{
    public class MatchRecord
    {
        public Guid Id { get; set; }
        public Guid WinnerId { get; set; }
        public int DurationInSeconds { get; set; }
        public DateTime EndedAt { get; set; } = DateTime.UtcNow;
        
        // List of all players who participated in the match
        public List<Guid> ParticipantIds { get; set; } = new();

        // Store the final state as a JSON document for post-match analytics
        public string FinalStateJson { get; set; } = string.Empty; 
    }
}
