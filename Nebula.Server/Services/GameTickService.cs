// File: Nebula.Server/Services/GameTickService.cs
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace Nebula.Server.Services
{
    public class GameTickService : BackgroundService
    {
        private readonly GameStateManager _gameStateManager;
        private readonly IHubContext<GameHub, IGameClient> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        
        // 10 ticks per second (100ms per tick)
        private const int TickIntervalMilliseconds = 100; 
        
        // Define our metrics
        private readonly Counter<long> _ticksProcessedCounter;
        private readonly Histogram<double> _tickDurationHistogram;

        public GameTickService(
            GameStateManager gameStateManager, 
            IHubContext<GameHub, IGameClient> hubContext,
            IServiceProvider serviceProvider,
            IMeterFactory meterFactory)
        {
            _gameStateManager = gameStateManager;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            
            // Create a custom meter that matches the name we registered in Program.cs
            var meter = meterFactory.Create("Nebula.GameServer");
            
            _ticksProcessedCounter = meter.CreateCounter<long>("nebula.server.ticks.total", description: "Total number of game ticks processed.");
            _tickDurationHistogram = meter.CreateHistogram<double>("nebula.server.tick.duration", unit: "ms", description: "Time taken to process a single 10Hz tick.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Use a PeriodicTimer for more precise timing than Task.Delay
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMilliseconds));
            var stopwatch = new Stopwatch();

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                stopwatch.Restart();

                var activeMatches = _gameStateManager.GetAllActiveMatches().ToList();

                if (!activeMatches.Any()) continue;

                // Process all matches concurrently
                await Parallel.ForEachAsync(activeMatches, stoppingToken, async (state, token) =>
                {
                    if (state.Status != GameStatus.InProgress) return;

                    state.CurrentTick++;

                    if (state.ActiveBot != null)
                    {
                        // Let the bot look at the board and queue actions before we process the queue
                        state.ActiveBot.Update(state);
                    }

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
                        // ANTI-CHEAT 1: Does the player actually exist in this match?
                        if (!state.Players.TryGetValue(action.PlayerId, out var player)) continue;

                        // ANTI-CHEAT 2: Is the match actually in progress? (Prevent post-game actions)
                        if (state.Status != GameStatus.InProgress) continue;

                        // ANTI-CHEAT 3: Is the player currently muted by an EMP? 
                        // (A hacked client might bypass the disabled UI button)
                        if (player.IsEmpMuted) continue;

                        if (action.Type == ActionType.DeployDrone)
                        {
                            const decimal DroneCost = 500.0m; // Hardcoded for now

                            // ANTI-CHEAT 4: Strict balance validation
                            if (player.Credits >= DroneCost)
                            {
                                // ANTI-CHEAT 5: Validate the target parameter is an expected value
                                if (action.TargetResource == "Ironium")
                                {
                                    player.Credits -= DroneCost;
                                    player.ActiveIroniumDrones++;
                                }
                                else if (action.TargetResource == "Plasma")
                                {
                                    player.Credits -= DroneCost;
                                    player.ActivePlasmaDrones++;
                                }
                                else
                                {
                                    // Unrecognized resource string. Hacker might be fuzzing the API.
                                    Console.WriteLine($"[Anti-Cheat] Invalid resource target from {action.PlayerId}.");
                                }
                            }
                        }
                        else if (action.Type == ActionType.BuildDefense && action.Defense == DefenseType.Firewall)
                        {
                            const double IroniumCost = 150.0;
                            const double PlasmaCost = 50.0;

                            // Check physical resource balances
                            if (player.Ironium >= IroniumCost && player.Plasma >= PlasmaCost)
                            {
                                player.Ironium -= IroniumCost;
                                player.Plasma -= PlasmaCost;
                                player.FirewallCharges++;
                                
                                // No system message needed here; the UI will just show the new charge
                            }
                        }
                        else if (action.Type == ActionType.UseSabotage && action.Sabotage == SabotageType.EMP)
                        {
                            const decimal EmpCost = 2000.0m;
                            
                            // ANTI-CHEAT 6: Self-targeting check and Target Existence check
                            if (action.TargetPlayerId == action.PlayerId) continue; 
                            
                            // Ensure the target actually exists in this match
                            if (state.Players.TryGetValue(action.TargetPlayerId, out var victim))
                            {
                                if (player.Credits >= EmpCost)
                                {
                                    player.Credits -= EmpCost;

                                    // NEW LOGIC: Intercept with Firewall
                                    if (victim.FirewallCharges > 0)
                                    {
                                        victim.FirewallCharges--;
                                        
                                        // Broadcast the interception so everyone knows what happened
                                        await _hubContext.Clients.Group(state.MatchId)
                                            .ReceiveSystemMessage($"NETWORK ALERT: {victim.PlayerName}'s Firewall absorbed an EMP from {player.PlayerName}!");
                                    }
                                    else
                                    {
                                        // Apply the EMP normally (from Phase 17)
                                        var newExpiration = state.CurrentTick + 100;
                                        victim.EmpExpirationTick = Math.Max(victim.EmpExpirationTick, newExpiration);
                                        
                                        await _hubContext.Clients.Group(state.MatchId)
                                            .ReceiveSystemMessage($"CRITICAL: {victim.PlayerName} was disabled by an EMP!");
                                    }
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
                        foreach (var marketEntry in state.MarketPrices)
                        {
                            var resource = marketEntry.Key;
                            var currentPrice = marketEntry.Value;
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

                    // Capture a replay keyframe every 10 ticks (1 second)
                    if (state.CurrentTick % 10 == 0)
                    {
                        var frame = new ReplayFrame
                        {
                            Tick = state.CurrentTick,
                            Market = new Dictionary<string, decimal>
                            {
                                { "Ironium", state.MarketPrices.ContainsKey("Ironium") ? state.MarketPrices["Ironium"] : 0 },
                                { "Plasma", state.MarketPrices.ContainsKey("Plasma") ? state.MarketPrices["Plasma"] : 0 }
                            }
                        };

                        foreach (var p in state.Players)
                        {
                            // Pack stats tightly into an array to save JSON space
                            frame.PlayerStats[p.Key] = new double[]
                            {
                                (double)p.Value.Credits,
                                p.Value.Ironium,
                                p.Value.Plasma,
                                p.Value.ActiveIroniumDrones,
                                p.Value.ActivePlasmaDrones,
                                p.Value.EmpExpirationTick
                            };
                        }

                        state.ReplayFrames.Add(frame);
                    }

                    const decimal VictoryThreshold = 50000.0m;
                    bool matchEnded = false;
                    string? winnerId = null;

                    foreach (var playerEntry in state.Players)
                    {
                        if (playerEntry.Value.Credits >= VictoryThreshold)
                        {
                            matchEnded = true;
                            winnerId = playerEntry.Key;
                            break; // First one to cross the line wins
                        }
                    }

                    if (matchEnded)
                    {
                        state.Status = GameStatus.Finished;
                        
                        // Broadcast the final definitive state
                        await _hubContext.Clients.Group(state.MatchId).ReceiveGameStateTick(state);
                        await _hubContext.Clients.Group(state.MatchId)
                            .ReceiveSystemMessage($"SIMULATION CONCLUDED. Winner: {state.Players[winnerId!].PlayerName}");

                        // 1. Send to the background DB worker
                        var persister = _serviceProvider.GetRequiredService<MatchPersisterService>();
                        persister.QueueFinishedMatch(state);

                        // 2. Remove from active memory to free up RAM
                        _gameStateManager.EndMatch(state.MatchId);
                        
                        // Skip further processing for this match this tick
                        return; 
                    }

                    // Broadcast the updated state to the specific match group
                    await _hubContext.Clients.Group(state.MatchId)
                        .ReceiveGameStateTick(state);
                });

                stopwatch.Stop();
                
                // Record the metrics instantly in memory
                _ticksProcessedCounter.Add(1);
                _tickDurationHistogram.Record(stopwatch.Elapsed.TotalMilliseconds);

                // Warning threshold: If a tick takes longer than 50ms, we are in danger 
                // of missing the 100ms window, which causes rubber-banding.
                if (stopwatch.ElapsedMilliseconds > 50)
                {
                    Console.WriteLine($"[WARN] Heavy Tick Detected: {stopwatch.ElapsedMilliseconds}ms");
                }
            }
        }
    }
}
