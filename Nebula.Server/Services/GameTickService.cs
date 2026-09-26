// File: Nebula.Server/Services/GameTickService.cs
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Shared.Interfaces;

namespace Nebula.Server.Services
{
    public class GameTickService : BackgroundService
    {
        private readonly GameStateManager _gameStateManager;
        private readonly IHubContext<GameHub, IGameClient> _hubContext;
        
        // 10 ticks per second (100ms per tick)
        private const int TickIntervalMilliseconds = 100; 

        public GameTickService(
            GameStateManager gameStateManager, 
            IHubContext<GameHub, IGameClient> hubContext)
        {
            _gameStateManager = gameStateManager;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Use a PeriodicTimer for more precise timing than Task.Delay
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMilliseconds));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var activeMatches = _gameStateManager.GetAllActiveMatches().ToList();

                if (!activeMatches.Any()) continue;

                // Process all matches concurrently
                await Parallel.ForEachAsync(activeMatches, stoppingToken, async (state, token) =>
                {
                    // 1. Process Game Logic (Mocked for now, implemented in Phase 11 & 12)
                    state.CurrentTick++;
                    
                    // (Example: Slowly give everyone passive credits every 10 ticks)
                    if (state.CurrentTick % 10 == 0)
                    {
                        foreach (var player in state.Players.Values)
                        {
                            player.Credits += 5; 
                        }
                    }

                    // 2. Broadcast the updated state to the specific match group
                    await _hubContext.Clients.Group(state.MatchId)
                        .ReceiveGameStateTick(state);
                });
            }
        }
    }
}
