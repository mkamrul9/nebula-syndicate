// File: Nebula.Server/Services/MatchmakingService.cs
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Server.Models;
using Nebula.Shared.Interfaces;

namespace Nebula.Server.Services
{
    public class MatchmakingService : BackgroundService
    {
        private readonly IHubContext<GameHub, IGameClient> _hubContext;
        private readonly ConcurrentQueue<QueuedPlayer> _queue = new();
        
        // We need 2 players for a 1v1 match
        private const int PlayersRequired = 2; 

        public MatchmakingService(IHubContext<GameHub, IGameClient> hubContext)
        {
            _hubContext = hubContext;
        }

        // The Hub calls this method to add players
        public void EnqueuePlayer(QueuedPlayer player)
        {
            _queue.Enqueue(player);
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

                // TODO in Phase 7: Register this match with the Game State Manager
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
    }
}
