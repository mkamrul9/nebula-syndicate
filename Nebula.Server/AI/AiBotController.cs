using Nebula.Shared.Models;
using Nebula.Shared.Interfaces;
using System.Collections.Concurrent;

namespace Nebula.Server.AI
{
    public class AiBotController : IAiBotController
    {
        public string BotId { get; } = Guid.NewGuid().ToString();
        private readonly ConcurrentQueue<PlayerAction> _matchQueue;
        private readonly bool _tutorialMode;

        private DateTime _nextAllowedActionTime = DateTime.UtcNow;
        private readonly Random _rand = new();

        public AiBotController(ConcurrentQueue<PlayerAction> matchQueue, bool tutorialMode = false)
        {
            _matchQueue = matchQueue;
            _tutorialMode = tutorialMode;
            if (_tutorialMode)
                _nextAllowedActionTime = DateTime.UtcNow.AddSeconds(4);
        }

        public void Update(GameState state)
        {
            if (DateTime.UtcNow < _nextAllowedActionTime) return;

            if (!state.Players.TryGetValue(BotId, out var myState)) return;
            if (myState.IsEmpMuted) return;

            // Keep the training rival behind so the first match can be won.
            if (_tutorialMode)
            {
                var humanCredits = state.Players
                    .Where(p => p.Key != BotId)
                    .Select(p => p.Value.Credits)
                    .DefaultIfEmpty(0)
                    .Max();
                if (myState.Credits > humanCredits - 4000m && myState.Credits > 8000m)
                {
                    _nextAllowedActionTime = DateTime.UtcNow.AddSeconds(3);
                    return;
                }
            }

            double ironiumUtility = 1.0;
            double plasmaUtility = 1.0;

            if (state.BasePrices.TryGetValue("Ironium", out var baseIronium) && state.MarketPrices.TryGetValue("Ironium", out var curIronium) && baseIronium > 0)
                ironiumUtility = (double)(curIronium / baseIronium);
            if (state.BasePrices.TryGetValue("Plasma", out var basePlasma) && state.MarketPrices.TryGetValue("Plasma", out var curPlasma) && basePlasma > 0)
                plasmaUtility = (double)(curPlasma / basePlasma);

            ironiumUtility *= _rand.NextDouble() * 0.4 + 0.8;
            plasmaUtility *= _rand.NextDouble() * 0.4 + 0.8;

            var sellIronAt = _tutorialMode ? (ironiumUtility >= 1.2 ? 30 : 60) : (ironiumUtility >= 1.15 ? 8 : 20);
            if (myState.Ironium >= sellIronAt)
            {
                var clip = (!_tutorialMode && ironiumUtility >= 1.15) ? myState.Ironium : Math.Min(_tutorialMode ? 15 : 25, myState.Ironium);
                Enqueue(new PlayerAction
                {
                    PlayerId = BotId,
                    Type = ActionType.SellResource,
                    TargetResource = "Ironium",
                    Amount = Math.Min(clip, 10000)
                });
                return;
            }

            var sellPlasmaAt = _tutorialMode ? (plasmaUtility >= 1.2 ? 12 : 25) : (plasmaUtility >= 1.15 ? 4 : 8);
            if (myState.Plasma >= sellPlasmaAt)
            {
                var clip = (!_tutorialMode && plasmaUtility >= 1.15) ? myState.Plasma : Math.Min(_tutorialMode ? 6 : 12, myState.Plasma);
                Enqueue(new PlayerAction
                {
                    PlayerId = BotId,
                    Type = ActionType.SellResource,
                    TargetResource = "Plasma",
                    Amount = Math.Min(clip, 10000)
                });
                return;
            }

            var opponentId = state.Players.Keys.FirstOrDefault(id => id != BotId);

            // 1. Build Defense: Non-tutorial bot protects itself with Firewalls
            if (!_tutorialMode && myState.FirewallCharges < 2 && myState.Ironium >= 150 && myState.Plasma >= 50 && _rand.NextDouble() < 0.6)
            {
                Enqueue(new PlayerAction
                {
                    PlayerId = BotId,
                    Type = ActionType.BuildDefense,
                    Defense = DefenseType.Firewall
                });
                return;
            }

            // 2. Tactical Sabotage: EMP on dump threat or lead; Market Virus to blind opponent
            if (opponentId != null && state.Players.TryGetValue(opponentId, out var opponent))
            {
                var opponentDumpThreat = opponent.Ironium + opponent.Plasma * 2 >= 35;
                var opponentAhead = opponent.Credits > myState.Credits;

                // Market Virus: blind opponent when they are ahead and bot has plenty of credits
                if (!_tutorialMode && myState.Credits >= 4500m && opponentAhead && !opponent.HasMarketVirus && _rand.NextDouble() < 0.45)
                {
                    Enqueue(new PlayerAction
                    {
                        PlayerId = BotId,
                        Type = ActionType.UseSabotage,
                        TargetPlayerId = opponentId,
                        Sabotage = SabotageType.MarketVirus
                    });
                    return;
                }

                // EMP: fire when dump threat detected or opponent is pulling ahead
                var empChance = _tutorialMode ? 0.08 : (opponentDumpThreat ? 0.75 : (opponentAhead ? 0.45 : 0.25));
                var empMinCredits = _tutorialMode ? 8000m : 2000m;
                if (myState.Credits >= empMinCredits && !opponent.IsEmpMuted && _rand.NextDouble() < empChance)
                {
                    Enqueue(new PlayerAction
                    {
                        PlayerId = BotId,
                        Type = ActionType.UseSabotage,
                        TargetPlayerId = opponentId,
                        Sabotage = SabotageType.EMP
                    });
                    return;
                }
            }

            // 3a. Power Surge: burst when the fleet is big, ideally stacked on a Grid Surge or to catch up
            var fleet = myState.ActiveIroniumDrones + myState.ActivePlasmaDrones;
            if (!_tutorialMode && !myState.IsPowerSurgeActive && myState.Credits >= 6000m && fleet >= 6)
            {
                var trailing = state.Players.Any(p => p.Key != BotId && p.Value.Credits > myState.Credits);
                var surgeChance = state.IsGlobalPowerSurge ? 0.7 : (trailing ? 0.35 : 0.15);
                if (_rand.NextDouble() < surgeChance)
                {
                    Enqueue(new PlayerAction
                    {
                        PlayerId = BotId,
                        Type = ActionType.ActivatePowerSurge
                    });
                    return;
                }
            }

            // 3. Drone Overcharge: boost harvesters when economy is rolling
            if (!_tutorialMode && myState.Credits >= 2500m && _rand.NextDouble() < 0.35)
            {
                if (myState.ActiveIroniumDrones >= 3 && !myState.IsIroniumOvercharged)
                {
                    Enqueue(new PlayerAction
                    {
                        PlayerId = BotId,
                        Type = ActionType.DroneOvercharge,
                        TargetResource = "Ironium"
                    });
                    return;
                }
                else if (myState.ActivePlasmaDrones >= 2 && !myState.IsPlasmaOvercharged)
                {
                    Enqueue(new PlayerAction
                    {
                        PlayerId = BotId,
                        Type = ActionType.DroneOvercharge,
                        TargetResource = "Plasma"
                    });
                    return;
                }
            }

            // 4. Drone Deployment
            const decimal DroneCost = 500m;
            if (myState.Credits >= DroneCost)
            {
                // Tutorial bot prefers iron and stays light on drones.
                if (_tutorialMode && myState.ActiveIroniumDrones + myState.ActivePlasmaDrones >= 4)
                {
                    _nextAllowedActionTime = DateTime.UtcNow.AddSeconds(2);
                    return;
                }

                var resource = _tutorialMode || ironiumUtility >= plasmaUtility ? "Ironium" : "Plasma";
                Enqueue(new PlayerAction
                {
                    PlayerId = BotId,
                    Type = ActionType.DeployDrone,
                    TargetResource = resource
                });
            }
        }

        private void Enqueue(PlayerAction action)
        {
            _matchQueue.Enqueue(action);
            var delay = _tutorialMode ? _rand.Next(1800, 3200) : _rand.Next(400, 1200);
            _nextAllowedActionTime = DateTime.UtcNow.AddMilliseconds(delay);
        }
    }
}
