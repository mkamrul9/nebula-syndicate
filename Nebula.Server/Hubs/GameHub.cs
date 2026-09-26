// File: Nebula.Server/Hubs/GameHub.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using Nebula.Server.Services;
using System.Collections.Concurrent;
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

        // Tracks the last action time for rate limiting (ConnectionId -> Timestamp)
        private static readonly ConcurrentDictionary<string, DateTime> _lastActionTimes = new();

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
            _lastActionTimes.TryRemove(Context.ConnectionId, out _);
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

        // Helper method to check rate limit
        private bool IsRateLimited()
        {
            var now = DateTime.UtcNow;
            if (_lastActionTimes.TryGetValue(Context.ConnectionId, out var lastAction))
            {
                // Only allow 1 action every 100 milliseconds (10 per second max)
                if ((now - lastAction).TotalMilliseconds < 100)
                {
                    Console.WriteLine($"[Anti-Cheat] Player {Context.UserIdentifier} rate-limited.");
                    return true; 
                }
            }
            _lastActionTimes[Context.ConnectionId] = now;
            return false;
        }

        // Client calls this to take an action in-game
        public async Task DispatchDrone(string targetNodeId)
        {
            if (IsRateLimited()) return;

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

        public async Task LaunchSabotage(string targetPlayerId, SabotageType type)
        {
            if (IsRateLimited()) return;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return;

            var match = _gameStateManager.GetMatch(matchId);
            if (match == null) return;

            match.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.UseSabotage,
                TargetPlayerId = targetPlayerId,
                Sabotage = type
            });
        }

        public async Task BuildDefense(DefenseType type)
        {
            if (IsRateLimited()) return;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.BuildDefense,
                Defense = type
            });
        }
    }
}
