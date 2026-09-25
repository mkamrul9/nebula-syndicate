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
    }
}
