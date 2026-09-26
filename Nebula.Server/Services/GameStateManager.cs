// File: Nebula.Server/Services/GameStateManager.cs
using System.Collections.Concurrent;
using Nebula.Shared.Models;

namespace Nebula.Server.Services
{
    public class GameStateManager
    {
        // Thread-safe collection holding all active matches
        private readonly ConcurrentDictionary<string, GameState> _activeGames = new();

        public void InitializeMatch(string matchId, IEnumerable<string> playerIds)
        {
            var state = new GameState
            {
                MatchId = matchId,
                Status = GameStatus.InProgress,
                CurrentTick = 0
            };

            // Initialize player default starting resources
            foreach (var id in playerIds)
            {
                state.Players[id] = new PlayerState
                {
                    PlayerName = $"Extractor_{id[..5]}", // Temporary name fallback
                    Credits = 1000m,
                    Ironium = 0.0,
                    Plasma = 0.0,
                    ActiveIroniumDrones = 2,
                    ActivePlasmaDrones = 0,
                    IsEmpMuted = false
                };
            }

            // Initialize the starting market economy for this specific match
            state.BasePrices["Ironium"] = 50.0m;
            state.MarketPrices["Ironium"] = 50.0m;
            state.MarketPressures["Ironium"] = 0.0m;

            state.BasePrices["Plasma"] = 150.0m;
            state.MarketPrices["Plasma"] = 150.0m;
            state.MarketPressures["Plasma"] = 0.0m;

            // Safely add to the concurrent dictionary
            _activeGames.TryAdd(matchId, state);
            Console.WriteLine($"[GameState] Match {matchId} initialized in memory.");
        }

        public GameState? GetMatch(string matchId)
        {
            _activeGames.TryGetValue(matchId, out var state);
            return state;
        }

        public IEnumerable<GameState> GetAllActiveMatches()
        {
            return _activeGames.Values;
        }

        public int GetActiveMatchCount()
        {
            return _activeGames.Count;
        }

        public void EndMatch(string matchId)
        {
            if (_activeGames.TryRemove(matchId, out _))
            {
                Console.WriteLine($"[GameState] Match {matchId} removed from memory.");
            }
        }
    }
}
