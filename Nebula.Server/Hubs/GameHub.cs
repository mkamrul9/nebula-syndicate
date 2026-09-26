// File: Nebula.Server/Hubs/GameHub.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using Nebula.Server.Services;
using Nebula.Server.Models;

namespace Nebula.Server.Hubs
{
    // [Authorize] ensures only logged-in users with a valid JWT can connect!
    [Authorize] 
    public class GameHub : Hub<IGameClient>
    {
        private readonly MatchmakingService _matchmaker;

        public GameHub(MatchmakingService matchmaker)
        {
            _matchmaker = matchmaker;
        }

        // Client calls this to queue for a match
        public async Task JoinMatchQueue()
        {
            var playerId = Context.UserIdentifier ?? "Guest"; 
            var connectionId = Context.ConnectionId;

            _matchmaker.EnqueuePlayer(new QueuedPlayer
            {
                PlayerId = playerId,
                ConnectionId = connectionId
            });
            
            await Clients.Caller.ReceiveSystemMessage("Entered matchmaking queue. Searching for rivals...");
        }

        // Client calls this to take an action in-game
        public async Task DispatchDrone(string targetNodeId)
        {
            var playerId = Context.UserIdentifier;
            // TODO in Phase 15: Add action to the game loop queue
        }

        public override async Task OnConnectedAsync()
        {
            // Log when a player connects to the socket
            var username = Context.User?.Identity?.Name ?? "Unknown";
            Console.WriteLine($"[SignalR] Player Connected: {username} ({Context.ConnectionId})");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            Console.WriteLine($"[SignalR] Player Disconnected: {Context.ConnectionId}");
            await base.OnDisconnectedAsync(exception);
        }
    }
}
