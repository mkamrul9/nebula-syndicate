// File: Nebula.Server/Services/GameTickService.cs
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Nebula.Server.Services
{
    public class GameTickService : BackgroundService
    {
        private readonly GameStateManager _gameStateManager;
        private readonly IHubContext<GameHub, IGameClient> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerConnectionTracker _tracker;
        
        private const int TickIntervalMilliseconds = 100; 
        
        // Configurable delay: 10Hz tick rate * 60 seconds * 3 minutes = 1800 ticks
        private const int SpectatorDelayTicks = 1800;
        private readonly ConcurrentDictionary<string, Queue<byte[]>> _spectatorBuffers = new();
        
        // Define our metrics
        private readonly Counter<long> _ticksProcessedCounter;
        private readonly Histogram<double> _tickDurationHistogram;

        public GameTickService(
            GameStateManager gameStateManager, 
            IHubContext<GameHub, IGameClient> hubContext,
            IServiceProvider serviceProvider,
            PlayerConnectionTracker tracker,
            IMeterFactory meterFactory)
        {
            _gameStateManager = gameStateManager;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _tracker = tracker;
            
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
                    try
                    {
                        await ProcessMatchTickAsync(state, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.WriteLine($"[WARN] Match {state.MatchId} tick failed: {ex.Message}");
                    }
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

        private async Task ProcessMatchTickAsync(GameState state, CancellationToken token)
        {
                    if (state.Status != GameStatus.InProgress) return;

                    state.CurrentTick++;
                    state.MatchDurationTicks = state.CurrentTick;

                    if (state.ActiveBot != null)
                    {
                        // Let the bot look at the board and queue actions before we process the queue
                        state.ActiveBot.Update(state);
                    }

                    // 1. EVALUATE DEBUFFS AND BUFFS FOR ALL PLAYERS
                    foreach (var p in state.Players.Values)
                    {
                        p.IsEmpMuted = state.CurrentTick < p.EmpExpirationTick;
                        p.IsIroniumOvercharged = state.CurrentTick < p.OverchargeIroniumUntilTick;
                        p.IsPlasmaOvercharged = state.CurrentTick < p.OverchargePlasmaUntilTick;
                        p.HasMarketVirus = state.CurrentTick < p.MarketVirusUntilTick;
                        p.IsPowerSurgeActive = state.CurrentTick < p.PowerSurgeUntilTick;
                        p.IsOnStreak = p.HotStreak >= 2;
                    }
                    state.IsGlobalPowerSurge = state.CurrentTick < state.GlobalPowerSurgeUntilTick;

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
                                    player.DronesDeployed++;
                                }
                                else if (action.TargetResource == "Plasma")
                                {
                                    player.Credits -= DroneCost;
                                    player.ActivePlasmaDrones++;
                                    player.DronesDeployed++;
                                }
                                else
                                {
                                    // Unrecognized resource string. Hacker might be fuzzing the API.
                                    Console.WriteLine($"[Anti-Cheat] Invalid resource target from {action.PlayerId}.");
                                }
                            }
                        }
                        else if (action.Type == ActionType.DroneOvercharge)
                        {
                            const decimal OverchargeCost = 1500.0m;
                            if (player.Credits >= OverchargeCost)
                            {
                                if (action.TargetResource == "Ironium")
                                {
                                    player.Credits -= OverchargeCost;
                                    player.OverchargeIroniumUntilTick = Math.Max(player.OverchargeIroniumUntilTick, state.CurrentTick) + 200;
                                    player.IsIroniumOvercharged = true;
                                    await _hubContext.Clients.Group(state.MatchId)
                                        .ReceiveSystemMessage($"⚡ OVERCHARGE ACTIVATED: {player.PlayerName} boosted Ironium harvesters (2x yield for 20s)!");
                                }
                                else if (action.TargetResource == "Plasma")
                                {
                                    player.Credits -= OverchargeCost;
                                    player.OverchargePlasmaUntilTick = Math.Max(player.OverchargePlasmaUntilTick, state.CurrentTick) + 200;
                                    player.IsPlasmaOvercharged = true;
                                    await _hubContext.Clients.Group(state.MatchId)
                                        .ReceiveSystemMessage($"⚡ OVERCHARGE ACTIVATED: {player.PlayerName} boosted Plasma harvesters (2x yield for 20s)!");
                                }
                            }
                        }
                        else if (action.Type == ActionType.SellResource)
                        {
                            if (!TrySellResource(state, player, action, out var sellNote))
                                continue;
                            if (!string.IsNullOrEmpty(sellNote))
                            {
                                await _hubContext.Clients.Group(state.MatchId)
                                    .ReceiveSystemMessage(sellNote);
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
                        else if (action.Type == ActionType.UseSabotage)
                        {
                            // ANTI-CHEAT 6: Self-targeting check and Target Existence check
                            if (action.TargetPlayerId == action.PlayerId) continue; 
                            
                            // Ensure the target actually exists in this match
                            if (state.Players.TryGetValue(action.TargetPlayerId, out var victim))
                            {
                                if (action.Sabotage == SabotageType.EMP)
                                {
                                    const decimal EmpCost = 2000.0m;
                                    if (player.Credits >= EmpCost)
                                    {
                                        player.Credits -= EmpCost;
                                        player.SabotagesUsed++;

                                        if (victim.FirewallCharges > 0)
                                        {
                                            victim.FirewallCharges--;
                                            await _hubContext.Clients.Group(state.MatchId)
                                                .ReceiveSystemMessage($"NETWORK ALERT: {victim.PlayerName}'s Firewall absorbed an EMP from {player.PlayerName}!");
                                        }
                                        else
                                        {
                                            var newExpiration = state.CurrentTick + 100;
                                            victim.EmpExpirationTick = Math.Max(victim.EmpExpirationTick, newExpiration);
                                            await _hubContext.Clients.Group(state.MatchId)
                                                .ReceiveSystemMessage($"CRITICAL: {victim.PlayerName} was disabled by an EMP!");
                                        }
                                    }
                                }
                                else if (action.Sabotage == SabotageType.MarketVirus)
                                {
                                    const decimal VirusCost = 3500.0m;
                                    if (player.Credits >= VirusCost)
                                    {
                                        player.Credits -= VirusCost;
                                        player.SabotagesUsed++;

                                        if (victim.FirewallCharges > 0)
                                        {
                                            victim.FirewallCharges--;
                                            await _hubContext.Clients.Group(state.MatchId)
                                                .ReceiveSystemMessage($"SECURITY ALERT: {victim.PlayerName}'s Firewall quarantined a Market Virus from {player.PlayerName}!");
                                        }
                                        else
                                        {
                                            victim.MarketVirusUntilTick = Math.Max(victim.MarketVirusUntilTick, state.CurrentTick) + 150;
                                            victim.HasMarketVirus = true;
                                            await _hubContext.Clients.Group(state.MatchId)
                                                .ReceiveSystemMessage($"CRITICAL INTRUSION: {victim.PlayerName}'s terminal was infected with a Market Virus! Data feed corrupted for 15s.");
                                        }
                                    }
                                }
                            }
                        }
                        else if (action.Type == ActionType.ActivatePowerSurge)
                        {
                            const decimal PowerSurgeCost = 4000.0m;
                            if (player.Credits >= PowerSurgeCost && !player.IsPowerSurgeActive)
                            {
                                player.Credits -= PowerSurgeCost;
                                player.PowerSurgeUntilTick = state.CurrentTick + 100; // 10 seconds
                                player.IsPowerSurgeActive = true;
                                await _hubContext.Clients.Group(state.MatchId)
                                    .ReceivePowerSurgeActivated(action.PlayerId, player.PlayerName);
                                await _hubContext.Clients.Group(state.MatchId)
                                    .ReceiveSystemMessage($"🚀 POWER SURGE! {player.PlayerName} activated 3x drone output for 10s!");
                            }
                        }
                        else if (action.Type == ActionType.EmergencySell)
                        {
                            // Sell all stockpiles at current market price (no bonus or penalty, pure panic)
                            if (!player.IsEmpMuted)
                            {
                                decimal emergencyPayout = 0;
                                if (state.MarketPrices.TryGetValue("Ironium", out var ironPrice) && player.Ironium >= 1)
                                {
                                    emergencyPayout += ironPrice * (decimal)Math.Floor(player.Ironium);
                                    state.MarketPressures["Ironium"] -= Math.Clamp((decimal)player.Ironium * 0.05m, 0m, 50m);
                                    player.Ironium = 0;
                                }
                                if (state.MarketPrices.TryGetValue("Plasma", out var plasmaPrice) && player.Plasma >= 1)
                                {
                                    emergencyPayout += plasmaPrice * (decimal)Math.Floor(player.Plasma);
                                    state.MarketPressures["Plasma"] -= Math.Clamp((decimal)player.Plasma * 0.05m, 0m, 50m);
                                    player.Plasma = 0;
                                }
                                // Emergency sell breaks the hot streak (panic moves reset momentum)
                                player.HotStreak = 0;
                                player.StreakMultiplier = 1.0m;
                                player.IsOnStreak = false;
                                player.Credits += emergencyPayout;
                                if (emergencyPayout > 0)
                                    await _hubContext.Clients.Group(state.MatchId)
                                        .ReceiveSystemMessage($"🚨 EMERGENCY DUMP: {player.PlayerName} liquidated all stockpiles for ₡{Math.Floor(emergencyPayout):N0} (streak lost).");
                            }
                        }
                     }

                    // Effects applied this tick must register immediately
                    foreach (var p in state.Players.Values)
                    {
                        p.IsEmpMuted = state.CurrentTick < p.EmpExpirationTick;
                        p.IsIroniumOvercharged = state.CurrentTick < p.OverchargeIroniumUntilTick;
                        p.IsPlasmaOvercharged = state.CurrentTick < p.OverchargePlasmaUntilTick;
                        p.HasMarketVirus = state.CurrentTick < p.MarketVirusUntilTick;
                        p.IsPowerSurgeActive = state.CurrentTick < p.PowerSurgeUntilTick;
                        p.IsOnStreak = p.HotStreak >= 2;
                    }

                    // Define our base rates (e.g., per second)
                    const double IroniumPerDronePerSec = 2.0;
                    const double PlasmaPerDronePerSec = 0.5;
                    
                    // Calculate how much to give PER TICK (10 ticks a second = divide by 10)
                    // 1000ms / TickIntervalMilliseconds (100) = 10 ticks per second
                    double ticksPerSecond = 1000.0 / TickIntervalMilliseconds; 
                    
                    double ironiumYieldPerTick = IroniumPerDronePerSec / ticksPerSecond;
                    double plasmaYieldPerTick = PlasmaPerDronePerSec / ticksPerSecond;

                    // Passive credit drip scales up over time for late-game catch-up
                    int passiveDrip = state.CurrentTick switch
                    {
                        < 600 => 5,    // 0-60s: ₡5/s
                        < 1800 => 8,   // 1-3min: ₡8/s
                        _ => 12        // 3min+: ₡12/s (late-game catch-up)
                    };

                    // Process all players in this match
                    foreach (var player in state.Players.Values)
                    {
                        // 1. Skip generation if the player is under a sabotage effect (e.g., EMP)
                        if (player.IsEmpMuted) continue;

                        // 2. Aggregate Resource Generation with Overcharge bonus (2x) + PowerSurge (3x)
                        double ironiumMult = player.IsIroniumOvercharged ? 2.0 : 1.0;
                        double plasmaMult = player.IsPlasmaOvercharged ? 2.0 : 1.0;

                        // Power Surge stacks on top of overcharge, giving 6x max burst
                        if (player.IsPowerSurgeActive)
                        {
                            ironiumMult *= 3.0;
                            plasmaMult *= 3.0;
                        }

                        // Global Power Surge event: all players get 1.5x
                        if (state.IsGlobalPowerSurge)
                        {
                            ironiumMult *= 1.5;
                            plasmaMult *= 1.5;
                        }

                        player.Ironium += player.ActiveIroniumDrones * ironiumYieldPerTick * ironiumMult;
                        player.Plasma += player.ActivePlasmaDrones * plasmaYieldPerTick * plasmaMult;
                        
                        // (Optional) Passive credit drip scaled by match duration
                        if (state.CurrentTick % (int)ticksPerSecond == 0) // Once every second
                        {
                            player.Credits += passiveDrip;
                        }
                    }

                    // We only want to update the market once per second to make charts readable.
                    // 10 ticks per second means modulo 10 == 0 is exactly one second.
                    if (state.CurrentTick % 10 == 0)
                    {
                        var random = Random.Shared;

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

                    // A shock every 20 seconds gives both sides a reason to sell or hold.
                    if (state.CurrentTick > 0 && state.CurrentTick % 200 == 0)
                    {
                        var shock = ApplyMarketShock(state);
                        if (shock != null)
                        {
                            await _hubContext.Clients.Group(state.MatchId).ReceiveSystemMessage(shock);
                        }
                    }

                    // Global Power Surge world event every 60 seconds (600 ticks)
                    if (state.CurrentTick > 0 && state.CurrentTick % 600 == 0 && !state.IsGlobalPowerSurge)
                    {
                        state.GlobalPowerSurgeUntilTick = state.CurrentTick + 150; // 15 seconds
                        state.IsGlobalPowerSurge = true;
                        await _hubContext.Clients.Group(state.MatchId)
                            .ReceiveMatchAlert("power_surge", "⚡ GRID SURGE! All harvesters running at 1.5x output for 15 seconds!");
                        await _hubContext.Clients.Group(state.MatchId)
                            .ReceiveSystemMessage("⚡ GRID SURGE EVENT! All drone output boosted 1.5x for 15s. Race to stockpile!");
                    }

                    // Track player dominance (leading tick streaks)
                    var leadingPlayerId = state.Players.OrderByDescending(p => p.Value.Credits).FirstOrDefault().Key;
                    foreach (var entry in state.Players)
                    {
                        if (entry.Key == leadingPlayerId)
                            entry.Value.DominanceTick++;
                        else
                            entry.Value.DominanceTick = Math.Max(0, entry.Value.DominanceTick - 1);
                    }

                    // Match timer alerts
                    if (state.CurrentTick == 1800) // 3 minutes
                        await _hubContext.Clients.Group(state.MatchId).ReceiveMatchAlert("warning", "⏱ 3 MINUTES ELAPSED — market volatility increasing!");
                    if (state.CurrentTick == 3600) // 6 minutes
                        await _hubContext.Clients.Group(state.MatchId).ReceiveMatchAlert("warning", "⏱ 6 MINUTES — late game. Prices are wild. Strike now or lose the line.");

                    foreach (var entry in state.Players)
                    {
                        var milestone = NextCreditMilestone(entry.Value.Credits, entry.Value.AnnouncedCreditMilestone);
                        if (milestone == 0) continue;
                        entry.Value.AnnouncedCreditMilestone = milestone;
                        await _hubContext.Clients.Group(state.MatchId)
                            .ReceiveSystemMessage($"{entry.Value.PlayerName} crossed ₡{milestone:N0}. The finish line is ₡{GameState.VictoryLine:N0}.");
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

                    string? winnerId = null;
                    string? endReason = null;

                    foreach (var playerEntry in state.Players)
                    {
                        if (playerEntry.Value.Credits >= GameState.VictoryLine)
                        {
                            winnerId = playerEntry.Key;
                            endReason = "SIMULATION CONCLUDED";
                            break; // First one to cross the line wins
                        }
                    }

                    if (winnerId == null)
                        winnerId = TryForfeitWinner(state, out endReason);

                    if (winnerId != null && state.Players.ContainsKey(winnerId))
                    {
                        state.Status = GameStatus.Finished;
                        state.WinnerId = winnerId;
                        
                        // Broadcast the final definitive state
                        await _hubContext.Clients.Group(state.MatchId).ReceiveGameStateTick(state);
                        await _hubContext.Clients.Group(state.MatchId)
                            .ReceiveSystemMessage($"{endReason}. Winner: {state.Players[winnerId].PlayerName}");

                        // 1. Send to the background DB worker
                        var persister = _serviceProvider.GetRequiredService<MatchPersisterService>();
                        persister.QueueFinishedMatch(state);

                        foreach (var playerId in state.Players.Keys)
                            _tracker.RemovePlayerFromMatch(playerId);

                        // 2. Remove from active memory to free up RAM
                        _gameStateManager.EndMatch(state.MatchId);
                        _spectatorBuffers.TryRemove(state.MatchId, out _);
                        
                        // Skip further processing for this match this tick
                        return; 
                    }

                    // Broadcast the updated state to the specific match group
                    await _hubContext.Clients.Group(state.MatchId)
                        .ReceiveGameStateTick(state);

                    // Slim snapshot: replay, bot, and the action queue must not be copied every tick.
                    var buffer = _spectatorBuffers.GetOrAdd(state.MatchId, _ => new Queue<byte[]>());
                    byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(CreateSpectatorSnapshot(state));
                    buffer.Enqueue(snapshot);

                    // 3. Broadcast to spectators only after the delay threshold is met
                    if (buffer.Count >= SpectatorDelayTicks)
                    {
                        var delayedSnapshot = buffer.Dequeue();
                        var delayedState = JsonSerializer.Deserialize<GameState>(delayedSnapshot);
                        if (delayedState != null)
                        {
                            await _hubContext.Clients.Group($"spectators_{state.MatchId}")
                                .ReceiveSpectatorTick(delayedState);
                        }
                    }
        }

        private static bool TrySellResource(GameState state, PlayerState player, PlayerAction action, out string? skillNote)
        {
            skillNote = null;
            if (action.TargetResource is not ("Ironium" or "Plasma"))
                return false;
            if (double.IsNaN(action.Amount) || double.IsInfinity(action.Amount) || action.Amount <= 0)
                return false;
            if (!state.MarketPrices.TryGetValue(action.TargetResource, out var price) || price <= 0)
                return false;

            var available = action.TargetResource == "Ironium" ? player.Ironium : player.Plasma;
            var qty = Math.Min(action.Amount, Math.Min(available, 10000));
            if (qty <= 0) return false;

            if (action.TargetResource == "Ironium") player.Ironium -= qty;
            else player.Plasma -= qty;

            var payout = price * (decimal)qty;
            var mood = "steady";
            if (state.BasePrices.TryGetValue(action.TargetResource, out var basePrice) && basePrice > 0)
            {
                var ratio = price / basePrice;
                if (ratio >= 1.15m)
                {
                    // HOT sell — increase streak, apply streak multiplier
                    player.HotStreak++;
                    player.StreakMultiplier = Math.Min(2.5m, 1.0m + (player.HotStreak - 1) * 0.25m);
                    payout *= 1.12m * player.StreakMultiplier; // Spike timing + streak bonus
                    player.HotSales++;
                    mood = "hot";
                }
                else if (ratio <= 0.90m)
                {
                    // COLD sell — break the streak
                    payout *= 0.88m;
                    player.ColdSales++;
                    player.HotStreak = 0;
                    player.StreakMultiplier = 1.0m;
                    mood = "cold";
                }
                else
                {
                    // Steady — slight streak decay but doesn't fully reset
                    player.HotStreak = Math.Max(0, player.HotStreak - 1);
                    player.StreakMultiplier = Math.Clamp(1.0m + (player.HotStreak - 1) * 0.25m, 1.0m, 2.5m);
                }
            }

            player.Credits += payout;
            player.IsOnStreak = player.HotStreak >= 2;
            var rounded = Math.Floor(payout);

            var streakNote = player.HotStreak >= 2 ? $" 🔥 x{player.HotStreak} STREAK! Multiplier: {player.StreakMultiplier:F2}x" : "";
            skillNote = mood switch
            {
                "hot" => $"🎯 TIMING WIN: {player.PlayerName} sold {qty:0} {action.TargetResource} on HOT for ₡{rounded} (+12%){streakNote}",
                "cold" => $"❄️ TIMING MISS: {player.PlayerName} sold {qty:0} {action.TargetResource} into COLD market for ₡{rounded} (−12%). Streak broken.",
                _ => $"{player.PlayerName} sold {qty:0} {action.TargetResource} for ₡{rounded}."
            };

            var pressure = Math.Clamp((decimal)qty * 0.05m, 0m, 25m);
            state.MarketPressures[action.TargetResource] -= pressure;
            return true;
        }

        private static string? TryForfeitWinner(GameState state, out string? reason)
        {
            reason = null;
            const int ForfeitAfterSeconds = 45;
            var now = DateTime.UtcNow;
            var forfeited = new HashSet<string>();

            foreach (var entry in state.Players)
            {
                var player = entry.Value;
                if (!player.IsConnected && player.DisconnectedAt.HasValue &&
                    (now - player.DisconnectedAt.Value).TotalSeconds >= ForfeitAfterSeconds)
                {
                    forfeited.Add(entry.Key);
                }
            }

            if (forfeited.Count == 0 || state.Players.Count == 0)
                return null;

            var survivor = state.Players
                .Where(p => !forfeited.Contains(p.Key))
                .OrderByDescending(p => p.Value.Credits)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(survivor.Key))
            {
                reason = "OPPONENT FORFEITED";
                return survivor.Key;
            }

            reason = "MATCH ABANDONED";
            return state.Players.OrderByDescending(p => p.Value.Credits).First().Key;
        }

        private static GameState CreateSpectatorSnapshot(GameState state)
        {
            return new GameState
            {
                MatchId = state.MatchId,
                Status = state.Status,
                CurrentTick = state.CurrentTick,
                WinnerId = state.WinnerId,
                Players = new Dictionary<string, PlayerState>(state.Players),
                MarketPrices = new Dictionary<string, decimal>(state.MarketPrices),
                MarketPressures = new Dictionary<string, decimal>(state.MarketPressures),
                BasePrices = new Dictionary<string, decimal>(state.BasePrices),
                MarketHeadline = state.MarketHeadline,
                MarketEventResource = state.MarketEventResource,
                MarketEventUntilTick = state.MarketEventUntilTick,
                MatchDurationTicks = state.MatchDurationTicks,
                GlobalPowerSurgeUntilTick = state.GlobalPowerSurgeUntilTick,
                IsGlobalPowerSurge = state.IsGlobalPowerSurge,
                IsTutorial = state.IsTutorial
            };
        }

        private static string? ApplyMarketShock(GameState state)
        {
            if (state.MarketPrices.Count == 0) return null;
            var resources = state.MarketPrices.Keys.ToList();
            var roll = Random.Shared.Next(5);

            if (roll == 0 && resources.Count >= 2)
            {
                // Gold Rush: both resources surge +25%
                foreach (var r in resources)
                {
                    state.MarketPrices[r] = Math.Round(Math.Clamp(state.MarketPrices[r] * 1.25m, 5m, 10000m), 2);
                }
                state.MarketHeadline = "GOLD RUSH! Heavy sector demand. All markets surging (+25%)!";
                state.MarketEventResource = "ALL";
            }
            else if (roll == 1 && resources.Count >= 2)
            {
                // Supply Chain Disruption: both crash -22%
                foreach (var r in resources)
                {
                    state.MarketPrices[r] = Math.Round(Math.Clamp(state.MarketPrices[r] * 0.78m, 5m, 10000m), 2);
                }
                state.MarketHeadline = "SUPPLY CHAIN COLLAPSE! Refinery dumps detected. All markets in freefall (-22%)!";
                state.MarketEventResource = "ALL";
            }
            else if (roll == 2 && resources.Count >= 2)
            {
                // Market Inversion: Swap prices between Ironium and Plasma
                var p1 = state.MarketPrices[resources[0]];
                var p2 = state.MarketPrices[resources[1]];
                state.MarketPrices[resources[0]] = p2;
                state.MarketPrices[resources[1]] = p1;
                state.MarketHeadline = $"MARKET INVERSION! HFT glitch swapped {resources[0]} and {resources[1]} rates!";
                state.MarketEventResource = "SWAP";
            }
            else
            {
                var resource = resources[Random.Shared.Next(resources.Count)];
                var price = state.MarketPrices[resource];
                var surge = Random.Shared.NextDouble() < 0.6;
                if (surge)
                {
                    state.MarketPrices[resource] = Math.Round(Math.Clamp(price * 1.30m, 5m, 10000m), 2);
                    state.MarketHeadline = $"{resource.ToUpperInvariant()} SURGE! Speculators flooding the exchange. Sell now for maximum cut!";
                }
                else
                {
                    state.MarketPrices[resource] = Math.Round(Math.Clamp(price * 0.75m, 5m, 10000m), 2);
                    state.MarketHeadline = $"{resource.ToUpperInvariant()} GLUT! Refinery overcapacity detected. Hold stockpile until rebound!";
                }
                state.MarketEventResource = resource;
            }

            state.MarketEventUntilTick = state.CurrentTick + 100;
            return state.MarketHeadline;
        }

        private static int NextCreditMilestone(decimal credits, int already)
        {
            int reached = credits >= 40000m ? 40000 : credits >= 25000m ? 25000 : credits >= 10000m ? 10000 : 0;
            return reached > already ? reached : 0;
        }
    }
}
