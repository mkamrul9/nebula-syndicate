// File: Nebula.Shared/Interfaces/IGameClient.cs
using Nebula.Shared.Models;

namespace Nebula.Shared.Interfaces
{
    public interface IGameClient
    {
        // Pushes the latest game state to the player
        Task ReceiveGameStateTick(GameState state);
        
        // Pushes a notification (e.g., "Player 2 hacked your drone!")
        Task ReceiveSystemMessage(string message);
        
        // Notifies the client that they successfully joined a match
        Task MatchJoined(string matchId);
        
        // New method to receive region
        Task AcknowledgeRegion(string regionName);
        
        // New method for structured chat
        Task ReceiveChatMessage(ChatMessage message); 
        
        // Pushes global notifications to the user
        Task ReceiveGlobalNotification(NotificationPayload payload);
        
        // Spectator mode delay broadcast
        Task ReceiveSpectatorTick(GameState state);
        
        // Moderation
        Task ForceDisconnect(string reason);
    }
}
