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
                    if (state.Status != GameStatus.InProgress) return;

                    state.CurrentTick++;

                    // Define our base rates (e.g., per second)
                    const double IroniumPerDronePerSec = 2.0;
                    const double PlasmaPerDronePerSec = 0.5;
                    
                    // Calculate how much to give PER TICK (10 ticks a second = divide by 10)
                    // 1000ms / TickIntervalMilliseconds (100) = 10 ticks per second
                    double ticksPerSecond = 1000.0 / TickIntervalMilliseconds; 
                    
                    double ironiumYieldPerTick = IroniumPerDronePerSec / ticksPerSecond;
                    double plasmaYieldPerTick = PlasmaPerDronePerSec / ticksPerSecond;

                    // Process all players in this match
                    foreach (var player in state.Players.Values)
                    {
                        // 1. Skip generation if the player is under a sabotage effect (e.g., EMP)
                        if (player.IsEmpMuted) continue;

                        // 2. Aggregate Resource Generation
                        player.Ironium += player.ActiveIroniumDrones * ironiumYieldPerTick;
                        player.Plasma += player.ActivePlasmaDrones * plasmaYieldPerTick;
                        
                        // (Optional) Passive credit drip for being alive
                        if (state.CurrentTick % (int)ticksPerSecond == 0) // Once every second
                        {
                            player.Credits += 5.0m;
                        }
                    }

                    // Broadcast the updated state to the specific match group
                    await _hubContext.Clients.Group(state.MatchId)
                        .ReceiveGameStateTick(state);
                });
            }
        }
    }
}
