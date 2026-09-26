using Nebula.Shared.Models;
using Nebula.Shared.Interfaces;
using System.Collections.Concurrent;
using System;

namespace Nebula.Server.AI
{
    public class AiBotController : IAiBotController
    {
        public string BotId { get; } = $"BOT_{Guid.NewGuid().ToString()[..8]}";
        private readonly ConcurrentQueue<PlayerAction> _matchQueue;
        
        private DateTime _nextAllowedActionTime = DateTime.UtcNow;
        private readonly Random _rand = new();

        public AiBotController(ConcurrentQueue<PlayerAction> matchQueue)
        {
            _matchQueue = matchQueue;
        }

        // This is called by the GameTickService once per tick
        public void Update(GameState state)
        {
            if (DateTime.UtcNow < _nextAllowedActionTime) return; // Simulating human reaction time

            if (!state.Players.TryGetValue(BotId, out var myState)) return;
            if (myState.IsEmpMuted) return; // Bound by the same rules!

            // 1. Calculate Utility Scores based on dynamic market conditions
            double ironiumUtility = 1.0;
            double plasmaUtility = 1.0;
            
            if (state.BasePrices.TryGetValue("Ironium", out var baseIronium) && state.MarketPrices.TryGetValue("Ironium", out var curIronium) && baseIronium > 0)
            {
                ironiumUtility = (double)(curIronium / baseIronium);
            }
            if (state.BasePrices.TryGetValue("Plasma", out var basePlasma) && state.MarketPrices.TryGetValue("Plasma", out var curPlasma) && basePlasma > 0)
            {
                plasmaUtility = (double)(curPlasma / basePlasma);
            }
            
            // 2. Add some random personality/noise so it doesn't play identically every time
            ironiumUtility *= _rand.NextDouble() * 0.5 + 0.8; 
            plasmaUtility *= _rand.NextDouble() * 0.5 + 0.8;

            // 3. Execute the most desirable action if we can afford it
            const decimal DroneCost = 500m;
            
            if (myState.Credits >= DroneCost)
            {
                if (ironiumUtility > plasmaUtility && ironiumUtility > 1.0)
                {
                    DispatchAction(ActionType.DeployDrone, "Ironium");
                }
                else if (plasmaUtility >= ironiumUtility && plasmaUtility > 1.0)
                {
                    DispatchAction(ActionType.DeployDrone, "Plasma");
                }
            }
        }

        private void DispatchAction(ActionType type, string target)
        {
            _matchQueue.Enqueue(new PlayerAction
            {
                PlayerId = BotId,
                Type = type,
                TargetResource = target
            });

            // Add artificial human delay (400ms to 1200ms) before the next action
            _nextAllowedActionTime = DateTime.UtcNow.AddMilliseconds(_rand.Next(400, 1200));
        }
    }
}
