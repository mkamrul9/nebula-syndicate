// File: Nebula.Server/Hubs/GameHub.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using Nebula.Server.Services;
using System.Collections.Concurrent;
using Nebula.Server.Models;
using Microsoft.EntityFrameworkCore;
using Nebula.Server.Data;

namespace Nebula.Server.Hubs
{
    [Authorize]
    public class GameHub : Hub<IGameClient>
    {
        private readonly MatchmakingService _matchmaker;
        private readonly PlayerConnectionTracker _tracker;
        private readonly GameStateManager _gameStateManager;
        private readonly string _serverRegion;
        private readonly NebulaDbContext _dbContext;

        private const string GlobalChannel = "Global";

        // Tracks the last action time for rate limiting (ConnectionId -> Timestamp)
        private static readonly ConcurrentDictionary<string, DateTime> _lastActionTimes = new();

        public GameHub(MatchmakingService matchmaker, PlayerConnectionTracker tracker, GameStateManager gameStateManager, IConfiguration config, NebulaDbContext dbContext)
        {
            _matchmaker = matchmaker;
            _tracker = tracker;
            _gameStateManager = gameStateManager;
            _serverRegion = config["SERVER_REGION"] ?? "Local-Dev";
            _dbContext = dbContext;
        }

        public override async Task OnConnectedAsync()
        {
            await Clients.Caller.AcknowledgeRegion(_serverRegion);
            
            var playerId = Context.UserIdentifier;
            
            // Add everyone to the Global chat group by default
            await Groups.AddToGroupAsync(Context.ConnectionId, GlobalChannel);
            
            if (playerId != null)
            {
                if (Guid.TryParse(playerId, out var userId))
                {
                    var player = await _dbContext.Players.FirstOrDefaultAsync(p => p.Id == userId);
                    if (player != null && player.IsBanned)
                    {
                        if (player.BanExpiresAtUTC == null || player.BanExpiresAtUTC > DateTime.UtcNow)
                        {
                            var reason = string.IsNullOrWhiteSpace(player.BanReason)
                                ? "Account suspended."
                                : player.BanReason;
                            await Clients.Caller.ForceDisconnect(reason);
                            Context.Abort();
                            return;
                        }

                        player.IsBanned = false;
                        player.BanReason = null;
                        player.BanExpiresAtUTC = null;
                        await _dbContext.SaveChangesAsync();
                    }
                }

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
                    else
                    {
                        _tracker.RemovePlayerFromMatch(playerId);
                    }
                }
                else
                {
                    _matchmaker.CancelQueuedConnection(Context.ConnectionId, playerId);
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
            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId))
                throw new HubException("Authentication required.");

            var result = _matchmaker.TryEnqueuePlayer(new QueuedPlayer
            {
                PlayerId = playerId,
                DisplayName = Context.User?.Identity?.Name ?? "Extractor",
                ConnectionId = Context.ConnectionId
            }, _serverRegion);

            switch (result)
            {
                case EnqueueResult.ShuttingDown:
                    await Clients.Caller.ReceiveSystemMessage("Server is shutting down for an update. Please reconnect.");
                    Context.Abort();
                    return;
                case EnqueueResult.AlreadyBusy:
                    throw new HubException("You are already queued or in a match.");
                case EnqueueResult.Rejected:
                    throw new HubException("Could not join the queue.");
            }
            
            await Clients.Caller.ReceiveSystemMessage("Entered matchmaking queue. Searching for rivals...");
        }

        // Instant soft bot match with in-arena coaching. Used for the first win.
        public async Task StartTutorialMatch()
        {
            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId))
                throw new HubException("Authentication required.");

            var existing = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (existing != null)
            {
                if (_gameStateManager.GetMatch(existing) == null)
                    _tracker.RemovePlayerFromMatch(playerId);
                else
                    throw new HubException("You are already in a match.");
            }

            _matchmaker.CancelQueuedConnection(Context.ConnectionId, playerId);

            var matchId = Guid.NewGuid().ToString();
            var actionQueue = new System.Collections.Concurrent.ConcurrentQueue<PlayerAction>();
            var bot = new Nebula.Server.AI.AiBotController(actionQueue, tutorialMode: true);

            _gameStateManager.InitializeMatch(matchId, new[] { playerId, bot.BotId });
            var state = _gameStateManager.GetMatch(matchId)
                ?? throw new HubException("Could not start the training match.");

            state.IsTutorial = true;
            state.ActiveBot = bot;
            state.PendingActions = actionQueue;

            if (state.Players.TryGetValue(playerId, out var human))
            {
                human.PlayerName = Context.User?.Identity?.Name ?? "Trainee";
                human.Credits = 2500m;
                human.ActiveIroniumDrones = 3;
                human.ActivePlasmaDrones = 1;
                human.Ironium = 180;
                human.Plasma = 60;
            }

            if (state.Players.TryGetValue(bot.BotId, out var rival))
            {
                rival.PlayerName = "Training Rig";
                rival.Credits = 800m;
                rival.ActiveIroniumDrones = 1;
                rival.ActivePlasmaDrones = 0;
                rival.Ironium = 0;
                rival.Plasma = 0;
            }

            _tracker.AssignPlayerToMatch(playerId, matchId);
            await Groups.AddToGroupAsync(Context.ConnectionId, matchId);
            await Clients.Caller.MatchJoined(matchId);
            await Clients.Caller.ReceiveGameStateTick(state);
            await Clients.Caller.ReceiveSystemMessage("🎓 TRAINING MATCH: Soft rival. Follow the coach and take the line.");
            Console.WriteLine($"[Matchmaker] Tutorial match {matchId} for {playerId}");
        }

        // Arena calls this after MatchJoined (or on refresh) so the connection is in the group
        // and the client gets a state frame immediately instead of waiting forever.
        public async Task EnterMatch(string matchId)
        {
            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId))
                throw new HubException("Authentication required.");

            var match = _gameStateManager.GetMatch(matchId);
            if (match == null || match.Status != GameStatus.InProgress)
            {
                _tracker.RemovePlayerFromMatch(playerId);
                throw new HubException("This match has ended. Return to the lobby and queue again.");
            }

            if (!match.Players.ContainsKey(playerId))
                throw new HubException("You are not a player in this match.");

            _tracker.AssignPlayerToMatch(playerId, matchId);
            await Groups.AddToGroupAsync(Context.ConnectionId, matchId);

            if (match.Players.TryGetValue(playerId, out var playerState))
            {
                playerState.IsConnected = true;
                playerState.DisconnectedAt = null;
            }

            await Clients.Caller.ReceiveGameStateTick(match);
            await Clients.Caller.ReceiveSystemMessage("Telemetry locked. Extraction live.");
        }

        public async Task WatchMatch(string matchId)
        {
            if (_gameStateManager.GetMatch(matchId) == null)
            {
                throw new HubException("Match not found or already ended.");
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, $"spectators_{matchId}");
            Console.WriteLine($"[Spectator] User {Context.ConnectionId} is watching match {matchId}");
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
        public Task DispatchDrone(string targetNodeId)
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;
            if (targetNodeId is not ("Ironium" or "Plasma")) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            if (match == null) return Task.CompletedTask;

            // We do NOT deduct credits here. We just queue the intent.
            match.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.DeployDrone,
                TargetResource = targetNodeId
            });

            return Task.CompletedTask;
        }

        public Task LaunchSabotage(string targetPlayerId, SabotageType type)
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            if (match == null) return Task.CompletedTask;

            match.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.UseSabotage,
                TargetPlayerId = targetPlayerId,
                Sabotage = type
            });

            return Task.CompletedTask;
        }

        public Task SellResource(string resource, double amount)
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;
            if (resource is not ("Ironium" or "Plasma")) return Task.CompletedTask;
            if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.SellResource,
                TargetResource = resource,
                Amount = amount
            });

            return Task.CompletedTask;
        }

        public Task BuildDefense(DefenseType type)
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.BuildDefense,
                Defense = type
            });

            return Task.CompletedTask;
        }

        public Task DroneOvercharge(string targetResource)
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;
            if (targetResource is not ("Ironium" or "Plasma")) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.DroneOvercharge,
                TargetResource = targetResource
            });

            return Task.CompletedTask;
        }

        // Client calls this to activate a Power Surge (x3 drone output for 10s, costs ₡4000)
        public Task ActivatePowerSurge()
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.ActivatePowerSurge
            });

            return Task.CompletedTask;
        }

        // Client calls this for an emergency sell-all of all resources at current prices
        public Task EmergencySell()
        {
            if (IsRateLimited()) return Task.CompletedTask;

            var playerId = Context.UserIdentifier;
            if (string.IsNullOrEmpty(playerId)) return Task.CompletedTask;

            var matchId = _tracker.GetActiveMatchIdForPlayer(playerId);
            if (matchId == null) return Task.CompletedTask;

            var match = _gameStateManager.GetMatch(matchId);
            match?.PendingActions.Enqueue(new PlayerAction
            {
                PlayerId = playerId,
                Type = ActionType.EmergencySell
            });

            return Task.CompletedTask;
        }
    }
}
