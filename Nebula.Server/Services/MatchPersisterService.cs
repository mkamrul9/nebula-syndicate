// File: Nebula.Server/Services/MatchPersisterService.cs
using System.Threading.Channels;
using Nebula.Shared.Models;
using Nebula.Server.Data;
using Nebula.Domain.Entities;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;

namespace Nebula.Server.Services
{
    public class MatchPersisterService : BackgroundService
    {
        // A thread-safe queue for finished matches
        private readonly Channel<GameState> _channel = Channel.CreateUnbounded<GameState>();
        private readonly IServiceProvider _serviceProvider;

        public MatchPersisterService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        // The fast GameTickService calls this to offload the DB work
        public void QueueFinishedMatch(GameState finalState)
        {
            _channel.Writer.TryWrite(finalState);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // The slow consumer loop
            await foreach (var state in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                // We must create a new scope because DbContext is Scoped, but this worker is a Singleton
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NebulaDbContext>();

                var winnerId = state.Players.OrderByDescending(p => p.Value.Credits).First().Key;

                var record = new MatchRecord
                {
                    Id = Guid.Parse(state.MatchId),
                    WinnerId = Guid.Parse(winnerId),
                    DurationInSeconds = state.CurrentTick / 10, // Assuming 10 ticks/sec
                    EndedAt = DateTime.UtcNow,
                    ParticipantIds = state.Players.Keys.Select(Guid.Parse).ToList(),
                    FinalStateJson = System.Text.Json.JsonSerializer.Serialize(state),
                    
                    // NEW: Serialize the compressed frame array
                    ReplayDataJson = System.Text.Json.JsonSerializer.Serialize(state.ReplayFrames) 
                };

                db.MatchRecords.Add(record);
                
                // Update player global stats and Quests
                foreach (var playerEntry in state.Players)
                {
                    var playerIdStr = playerEntry.Key;
                    var playerState = playerEntry.Value;
                    var playerId = Guid.Parse(playerIdStr);
                    
                    var profile = await db.Players.FindAsync(new object[] { playerId }, cancellationToken: stoppingToken);
                    if (profile != null)
                    {
                        profile.TotalMatchesPlayed++;
                        if (playerIdStr == winnerId) profile.TotalWins++;
                    }

                    // Fetch active (unexpired) quests for this player
                    var activeQuests = await db.Set<PlayerQuest>()
                        .Where(q => q.PlayerId == playerId && q.ExpirationDateUTC > DateTime.UtcNow && !q.IsClaimed)
                        .ToListAsync(stoppingToken);

                    foreach (var quest in activeQuests)
                    {
                        if (quest.IsCompleted) continue;

                        switch (quest.Type)
                        {
                            case QuestType.PlayMatches:
                                quest.CurrentValue += 1;
                                break;
                            case QuestType.WinMatches:
                                if (playerIdStr == winnerId) quest.CurrentValue += 1;
                                break;
                            case QuestType.DeployDrones:
                                // Tally the total drones they had at the end of the match
                                quest.CurrentValue += (playerState.ActiveIroniumDrones + playerState.ActivePlasmaDrones);
                                break;
                            case QuestType.UseSabotage:
                                // To do: tracking sabotage usage might require extending the replay frame or state.
                                break;
                        }

                        // Clamp the value so it doesn't exceed the target
                        if (quest.CurrentValue > quest.TargetValue) 
                            quest.CurrentValue = quest.TargetValue;
                    }
                }

                await db.SaveChangesAsync(stoppingToken);
                
                var redisDb = _serviceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
                var winnerUsername = state.Players[winnerId].PlayerName; 
                await redisDb.SortedSetIncrementAsync("leaderboard:wins", winnerUsername, 1);
                
                Console.WriteLine($"[DB] Match {state.MatchId} saved to PostgreSQL and Redis.");
            }
        }
    }
}
