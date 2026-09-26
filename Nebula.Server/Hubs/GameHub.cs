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

        private const string GlobalChannel = "Global";

        public GameHub(MatchmakingService matchmaker, PlayerConnectionTracker tracker, GameStateManager gameStateManager)
        {
            _matchmaker = matchmaker;
            _tracker = tracker;
            _gameStateManager = gameStateManager;
        }

        public override async Task OnConnectedAsync()
        {
            var playerId = Context.UserIdentifier;
            
            // Add everyone to the Global chat group by default
            await Groups.AddToGroupAsync(Context.ConnectionId, GlobalChannel);
            
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

        // The client calls this method when a user hits "Send"
        public async Task SendChatMessage(string message, string channel)
        {
            var playerId = Context.UserIdentifier;
            var username = Context.User?.Identity?.Name ?? "Unknown Extractor";

            // Basic validation
            if (string.IsNullOrWhiteSpace(message) || message.Length > 200) return;

            var chatMessage = new ChatMessage
            {
                SenderName = username,
                Message = message,
                Channel = channel,
                IsSystemEvent = false
            };

            if (channel == GlobalChannel)
            {
                // Broadcast to everyone in the Lobby
                await Clients.Group(GlobalChannel).ReceiveChatMessage(chatMessage);
            }
            else if (channel == "Match")
            {
                // Find out which match this player is in
                var matchId = _tracker.GetActiveMatchIdForPlayer(playerId!);
                if (matchId != null)
                {
                    // Broadcast ONLY to the players in this specific match
                    await Clients.Group(matchId).ReceiveChatMessage(chatMessage);
                }
            }
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
            if (string.IsNullOrEmpty(playerId)) return;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return;

            var match = _gameStateManager.GetMatch(matchId);
            if (match == null) return;

            // We do NOT deduct credits here. We just queue the intent.
            match.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.DeployDrone,
                TargetResource = targetNodeId
            });
        }
    }
}
