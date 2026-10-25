// File: Nebula.Server/Services/MatchmakingService.cs
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Server.Models;
using Nebula.Shared.Interfaces;
using Nebula.Server.AI;
using Nebula.Shared.Models;

namespace Nebula.Server.Services
{
    public class MatchmakingService : BackgroundService
    {
        private readonly IHubContext<GameHub, IGameClient> _hubContext;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedPlayer>> _regionalQueues = new();
        private bool _isAcceptingNewMatches = true;
        
        // We need 2 players for a 1v1 match
        private const int PlayersRequired = 2; 

        private readonly GameStateManager _gameStateManager;
        private readonly PlayerConnectionTracker _tracker;
        private readonly ConcurrentDictionary<string, byte> _queuedPlayerIds = new();
        private readonly ConcurrentDictionary<string, byte> _cancelledConnections = new();

        public MatchmakingService(
            IHubContext<GameHub, IGameClient> hubContext, 
            GameStateManager gameStateManager,
            PlayerConnectionTracker tracker,
            IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            _gameStateManager = gameStateManager;
            _tracker = tracker;

            // Intercept the server shutdown signal (e.g., from Kubernetes/Azure)
            appLifetime.ApplicationStopping.Register(() =>
            {
                Console.WriteLine("[LiveOps] Shutdown signal received. Draining server...");
                
                // Instantly stop accepting new players into this node's queue
                _isAcceptingNewMatches = false;
            });
        }

        // The background worker needs to know when it is safe to finally exit
        public bool IsReadyToShutdown()
        {
            return !_isAcceptingNewMatches && _gameStateManager.GetActiveMatchCount() == 0;
        }

        public EnqueueResult TryEnqueuePlayer(QueuedPlayer player, string region)
        {
            if (!_isAcceptingNewMatches)
                return EnqueueResult.ShuttingDown;

            if (string.IsNullOrWhiteSpace(player.PlayerId))
                return EnqueueResult.Rejected;

            var existingMatch = _tracker.GetActiveMatchIdForPlayer(player.PlayerId);
            if (existingMatch != null)
            {
                // Stale after a server restart / finished match that left a tracker entry.
                if (_gameStateManager.GetMatch(existingMatch) == null)
                    _tracker.RemovePlayerFromMatch(player.PlayerId);
                else
                    return EnqueueResult.AlreadyBusy;
            }

            if (!_queuedPlayerIds.TryAdd(player.PlayerId, 0))
                return EnqueueResult.AlreadyBusy;

            var queue = _regionalQueues.GetOrAdd(region, _ => new ConcurrentQueue<QueuedPlayer>());
            queue.Enqueue(player);
            return EnqueueResult.Queued;
        }

        public void CancelQueuedConnection(string connectionId, string playerId)
        {
            _cancelledConnections[connectionId] = 0;
            _queuedPlayerIds.TryRemove(playerId, out _);
        }

        // This runs continuously in the background
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var (region, queue) in _regionalQueues)
                {
                    if (queue.Count >= PlayersRequired)
                    {
                        await TryCreateMatch(queue, region);
                    }
                    else if (queue.Count == 1) // Only one player waiting
                    {
                        if (queue.TryPeek(out var player))
                        {
                            if ((DateTime.UtcNow - player.JoinedAt).TotalSeconds > 5)
                            {
                                await TryCreateBotMatch(queue, region);
                            }
                        }
                    }
                }

                // Prevent the tight loop from maxing out the CPU
                await Task.Delay(1000, stoppingToken); 
            }
        }

        private async Task TryCreateMatch(ConcurrentQueue<QueuedPlayer> queue, string region)
        {
            var matchedPlayers = new List<QueuedPlayer>();

            // Try to dequeue the required number of players
            while (matchedPlayers.Count < PlayersRequired && queue.TryDequeue(out var player))
            {
                if (IsCancelled(player))
                    continue;
                matchedPlayers.Add(player);
            }

            if (matchedPlayers.Count == PlayersRequired)
            {
                var matchId = Guid.NewGuid().ToString();
                Console.WriteLine($"[Matchmaker] Match {matchId} created for {PlayersRequired} players.");

                var playerIds = matchedPlayers.Select(p => p.PlayerId).ToList();
                _gameStateManager.InitializeMatch(matchId, playerIds);
                var created = _gameStateManager.GetMatch(matchId);
                if (created != null)
                {
                    foreach (var queued in matchedPlayers)
                    {
                        if (created.Players.TryGetValue(queued.PlayerId, out var playerState) &&
                            !string.IsNullOrWhiteSpace(queued.DisplayName))
                        {
                            playerState.PlayerName = queued.DisplayName;
                        }
                    }
                }

                foreach (var id in playerIds)
                {
                    _queuedPlayerIds.TryRemove(id, out _);
                    _tracker.AssignPlayerToMatch(id, matchId);
                }

                foreach (var p in matchedPlayers)
                {
                    await _hubContext.Groups.AddToGroupAsync(p.ConnectionId, matchId);
                    await _hubContext.Clients.Client(p.ConnectionId).MatchJoined(matchId);
                    if (created != null)
                        await _hubContext.Clients.Client(p.ConnectionId).ReceiveGameStateTick(created);
                    await _hubContext.Clients.Client(p.ConnectionId)
                        .ReceiveSystemMessage("Match found! Prepare for extraction.");
                }
            }
            else
            {
                // If we didn't get enough players (e.g., someone dequeued), put them back
                foreach (var p in matchedPlayers)
                {
                    queue.Enqueue(p);
                }
            }
        }

        private bool IsCancelled(QueuedPlayer player)
        {
            if (!_cancelledConnections.TryRemove(player.ConnectionId, out _))
                return false;

            _queuedPlayerIds.TryRemove(player.PlayerId, out _);
            return true;
        }

        private async Task TryCreateBotMatch(ConcurrentQueue<QueuedPlayer> queue, string region)
        {
            if (queue.TryDequeue(out var human))
            {
                if (IsCancelled(human))
                    return;

                var matchId = Guid.NewGuid().ToString();
                var actionQueue = new ConcurrentQueue<PlayerAction>();
                var bot = new AiBotController(actionQueue);

                Console.WriteLine($"[Matchmaker] Player {human.PlayerId} timed out. Provisioning AI Bot Match {matchId}.");

                var playerIds = new[] { human.PlayerId, bot.BotId };
                _gameStateManager.InitializeMatch(matchId, playerIds);

                var state = _gameStateManager.GetMatch(matchId);
                if (state != null)
                {
                    state.ActiveBot = bot;
                    state.PendingActions = actionQueue;
                    state.Players[human.PlayerId].PlayerName = string.IsNullOrWhiteSpace(human.DisplayName)
                        ? "Human Extractor"
                        : human.DisplayName;
                    state.Players[bot.BotId].PlayerName = "Syndicate AI";
                }

                _queuedPlayerIds.TryRemove(human.PlayerId, out _);
                _tracker.AssignPlayerToMatch(human.PlayerId, matchId);

                await _hubContext.Groups.AddToGroupAsync(human.ConnectionId, matchId);
                await _hubContext.Clients.Client(human.ConnectionId).MatchJoined(matchId);
                if (state != null)
                    await _hubContext.Clients.Client(human.ConnectionId).ReceiveGameStateTick(state);
                await _hubContext.Clients.Client(human.ConnectionId)
                    .ReceiveSystemMessage("Match found! Prepare for extraction.");
            }
        }
    }

    public enum EnqueueResult
    {
        Queued,
        ShuttingDown,
        AlreadyBusy,
        Rejected
    }
}
