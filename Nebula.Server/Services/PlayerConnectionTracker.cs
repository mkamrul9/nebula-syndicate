// File: Nebula.Server/Services/PlayerConnectionTracker.cs
using System.Collections.Concurrent;

namespace Nebula.Server.Services
{
    public class PlayerConnectionTracker
    {
        // Map ConnectionId -> PlayerId
        private readonly ConcurrentDictionary<string, string> _connections = new();
        // Map PlayerId -> MatchId (to quickly find which match a reconnecting player belongs to)
        private readonly ConcurrentDictionary<string, string> _playerMatches = new();

        public void AddConnection(string connectionId, string playerId) 
            => _connections.TryAdd(connectionId, playerId);

        public string? RemoveConnection(string connectionId)
        {
            _connections.TryRemove(connectionId, out var playerId);
            return playerId;
        }

        public void AssignPlayerToMatch(string playerId, string matchId)
            => _playerMatches.AddOrUpdate(playerId, matchId, (_, _) => matchId);

        public string? GetActiveMatchIdForPlayer(string playerId)
        {
            _playerMatches.TryGetValue(playerId, out var matchId);
            return matchId;
        }
        
        public void RemovePlayerFromMatch(string playerId)
            => _playerMatches.TryRemove(playerId, out _);
    }
}
