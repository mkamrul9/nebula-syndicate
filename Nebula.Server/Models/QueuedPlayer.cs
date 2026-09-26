// File: Nebula.Server/Models/QueuedPlayer.cs
namespace Nebula.Server.Models
{
    public class QueuedPlayer
    {
        public string PlayerId { get; set; } = string.Empty;
        public string ConnectionId { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    }
}
