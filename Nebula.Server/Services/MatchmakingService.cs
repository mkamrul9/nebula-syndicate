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
        private readonly ConcurrentQueue<QueuedPlayer> _queue = new();
        private bool _isAcceptingNewMatches = true;
        
        // We need 2 players for a 1v1 match
        private const int PlayersRequired = 2; 

        private readonly GameStateManager _gameStateManager;
        private readonly PlayerConnectionTracker _tracker;

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

        // The Hub calls this method to add players
        public bool TryEnqueuePlayer(QueuedPlayer player)
        {
            if (!_isAcceptingNewMatches)
            {
                return false;
            }

            _queue.Enqueue(player);
            return true;
        }

        // This runs continuously in the background
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_queue.Count >= PlayersRequired)
                {
                    await TryCreateMatch();
                }
                else if (_queue.Count == 1) // Only one player waiting
                {
                    if (_queue.TryPeek(out var player))
                    {
                        if ((DateTime.UtcNow - player.JoinedAt).TotalSeconds > 60)
                        {
                            await TryCreateBotMatch();
                        }
                    }
                }

                // Prevent the tight loop from maxing out the CPU
                await Task.Delay(1000, stoppingToken); 
            }
        }

        private async Task TryCreateMatch()
        {
            var matchedPlayers = new List<QueuedPlayer>();

            // Try to dequeue the required number of players
            while (matchedPlayers.Count < PlayersRequired && _queue.TryDequeue(out var player))
            {
                matchedPlayers.Add(player);
            }

            if (matchedPlayers.Count == PlayersRequired)
            {
                // Create a unique Match ID
                var matchId = Guid.NewGuid().ToString();

                Console.WriteLine($"[Matchmaker] Match {matchId} created for {PlayersRequired} players.");

                // Group players in SignalR so we can broadcast to just this match
                foreach (var p in matchedPlayers)
                {
                    await _hubContext.Groups.AddToGroupAsync(p.ConnectionId, matchId);
                    
                    // Notify the specific client they found a match
                    await _hubContext.Clients.Client(p.ConnectionId).MatchJoined(matchId);
                    await _hubContext.Clients.Client(p.ConnectionId)
                        .ReceiveSystemMessage("Match found! Prepare for extraction.");
                }
                var playerIds = matchedPlayers.Select(p => p.PlayerId);
                _gameStateManager.InitializeMatch(matchId, playerIds);
                foreach (var id in playerIds)
                {
                    _tracker.AssignPlayerToMatch(id, matchId);
                }
            }
            else
            {
                // If we didn't get enough players (e.g., someone dequeued), put them back
                foreach (var p in matchedPlayers)
                {
                    _queue.Enqueue(p);
                }
            }
        }

        private async Task TryCreateBotMatch()
        {
            if (_queue.TryDequeue(out var human))
            {
                var matchId = Guid.NewGuid().ToString();
                var queue = new ConcurrentQueue<PlayerAction>();
                var bot = new AiBotController(queue);

                Console.WriteLine($"[Matchmaker] Player {human.PlayerId} timed out. Provisioning AI Bot Match {matchId}.");

                await _hubContext.Groups.AddToGroupAsync(human.ConnectionId, matchId);
                
                await _hubContext.Clients.Client(human.ConnectionId).MatchJoined(matchId);
                await _hubContext.Clients.Client(human.ConnectionId)
                    .ReceiveSystemMessage("Match found! Prepare for extraction.");

                var playerIds = new[] { human.PlayerId, bot.BotId };
                _gameStateManager.InitializeMatch(matchId, playerIds);
                
                var state = _gameStateManager.GetMatch(matchId);
                if (state != null)
                {
                    state.ActiveBot = bot;
                    state.PendingActions = queue;
                    state.Players[human.PlayerId].PlayerName = "Human Extractor";
                    state.Players[bot.BotId].PlayerName = "Syndicate AI";
                }

                _tracker.AssignPlayerToMatch(human.PlayerId, matchId);
            }
        }
    }
}
