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

                    // 1. EVALUATE DEBUFFS FOR ALL PLAYERS
                    foreach (var p in state.Players.Values)
                    {
                        // Automatically flips to false when the current tick surpasses the expiration
                        p.IsEmpMuted = state.CurrentTick < p.EmpExpirationTick;
                    }

                    // 2. PROCESS ACTION QUEUE
                    // Drain the queue of all actions that came in during the last 100ms
                    while (state.PendingActions.TryDequeue(out var action))
                    {
                        if (!state.Players.TryGetValue(action.PlayerId, out var player)) continue;

                        if (action.Type == ActionType.DeployDrone)
                        {
                            const decimal DroneCost = 500.0m; // Hardcoded for now

                            // Check exact balance at the moment of execution
                            if (player.Credits >= DroneCost)
                            {
                                player.Credits -= DroneCost;

                                if (action.TargetResource == "Ironium")
                                {
                                    player.ActiveIroniumDrones++;
                                }
                                else if (action.TargetResource == "Plasma")
                                {
                                    player.ActivePlasmaDrones++;
                                }
                            }
                        }
                        else if (action.Type == ActionType.UseSabotage && action.Sabotage == SabotageType.EMP)
                        {
                            const decimal EmpCost = 2000.0m;
                            
                            // Ensure the target actually exists in this match
                            if (state.Players.TryGetValue(action.TargetPlayerId, out var victim))
                            {
                                if (player.Credits >= EmpCost)
                                {
                                    player.Credits -= EmpCost;
                                    
                                    // 100 ticks = 10 seconds (assuming 10 ticks per second)
                                    var newExpiration = state.CurrentTick + 100;
                                    
                                    // Max clamping prevents overlapping EMPs from stacking infinitely
                                    victim.EmpExpirationTick = Math.Max(victim.EmpExpirationTick, newExpiration);
                                }
                            }
                        }
                        // ... logic for SellResource will go here later
                    }

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

                    // We only want to update the market once per second to make charts readable.
                    // 10 ticks per second means modulo 10 == 0 is exactly one second.
                    if (state.CurrentTick % 10 == 0)
                    {
                        var random = new Random(); // Note: Use thread-local Random in production

                        // Process each resource market
                        foreach (var resource in state.MarketPrices.Keys.ToList())
                        {
                            var currentPrice = state.MarketPrices[resource];
                            var basePrice = state.BasePrices[resource];
                            var currentPressure = state.MarketPressures[resource];

                            // 1. Mean Reversion: The market naturally wants to pull back to its BasePrice.
                            // If price is high, reversion is negative. If low, reversion is positive.
                            var reversionForce = (basePrice - currentPrice) * 0.05m; // 5% pull per second

                            // 2. Random Volatility: A slight noise between -1% and +1%
                            var volatility = currentPrice * (decimal)((random.NextDouble() * 0.02) - 0.01);

                            // 3. Calculate New Price
                            var newPrice = currentPrice + reversionForce + volatility + currentPressure;

                            // 4. Hard Clamping: Prevent negative prices and hyperinflation
                            newPrice = Math.Clamp(newPrice, 5.0m, 10000.0m);
                            state.MarketPrices[resource] = Math.Round(newPrice, 2); // 2 decimal places max

                            // 5. Decay Pressure: Player influence fades over time
                            state.MarketPressures[resource] *= 0.90m; // 10% decay per second
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
