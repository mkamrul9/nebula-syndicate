// File: Nebula.Server/Hubs/GameHub.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using Nebula.Server.Services;
using Nebula.Server.Models;

namespace Nebula.Server.Hubs
{
    [Authorize]
    public class GameHub : Hub<IGameClient>
    {
        private readonly MatchmakingService _matchmaker;
        private readonly PlayerConnectionTracker _tracker;
        private readonly GameStateManager _gameStateManager;

        public GameHub(MatchmakingService matchmaker, PlayerConnectionTracker tracker, GameStateManager gameStateManager)
        {
            _matchmaker = matchmaker;
            _tracker = tracker;
            _gameStateManager = gameStateManager;
        }

        public override async Task OnConnectedAsync()
        {
            var playerId = Context.UserIdentifier;
            if (playerId != null)
            {
                _tracker.AddConnection(Context.ConnectionId, playerId);
                
                // Check if this player is reconnecting to an active match
                var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
                if (matchId != null)
                {
                    var match = _gameStateManager.GetMatch(matchId);
                    if (match != null)
                    {
                        // Re-add the new connection to the SignalR Group
                        await Groups.AddToGroupAsync(Context.ConnectionId, matchId);
                        
                        // Update game state
                        if (match.Players.TryGetValue(playerId, out var playerState))
                        {
                            playerState.IsConnected = true;
                            playerState.DisconnectedAt = null;
                        }
                        
                        await Clients.Group(matchId).ReceiveSystemMessage($"Player {playerId[..5]} reconnected.");
                    }
                }
            }
            
            var username = Context.User?.Identity?.Name ?? "Unknown";
            Console.WriteLine($"[SignalR] Player Connected: {username} ({Context.ConnectionId})");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var playerId = _tracker.RemoveConnection(Context.ConnectionId);
            
            if (playerId != null)
            {
                var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
                if (matchId != null)
                {
                    var match = _gameStateManager.GetMatch(matchId);
                    if (match != null && match.Players.TryGetValue(playerId, out var playerState))
                    {
                        // Mark as disconnected, but don't destroy the match yet
                        playerState.IsConnected = false;
                        playerState.DisconnectedAt = DateTime.UtcNow;
                        
                        await Clients.Group(matchId).ReceiveSystemMessage($"Player {playerId[..5]} disconnected. Waiting for reconnect...");
                    }
                }
            }
            
            Console.WriteLine($"[SignalR] Player Disconnected: {Context.ConnectionId}");
            await base.OnDisconnectedAsync(exception);
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
    }
}
