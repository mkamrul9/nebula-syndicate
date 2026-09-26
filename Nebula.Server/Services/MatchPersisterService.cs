// File: Nebula.Server/Services/MatchPersisterService.cs
using System.Threading.Channels;
using Nebula.Shared.Models;
using Nebula.Server.Data;
using Nebula.Domain.Entities;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Nebula.Server.Services
{
    public class MatchPersisterService : BackgroundService
    {
        // A thread-safe queue for finished matches
        private readonly Channel<GameState> _channel = Channel.CreateUnbounded<GameState>();
        private readonly IServiceProvider _serviceProvider;
        private readonly IFeatureManager _featureManager;

        public MatchPersisterService(IServiceProvider serviceProvider, IFeatureManager featureManager)
        {
            _serviceProvider = serviceProvider;
            _featureManager = featureManager;
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

                // 1. Identify the currently active season
                var activeSeason = await db.Set<Season>()
                    .FirstOrDefaultAsync(s => s.StartDateUTC <= DateTime.UtcNow && s.EndDateUTC >= DateTime.UtcNow, stoppingToken);

                if (activeSeason != null)
                {
                    // Base XP is tied to match length (e.g., 10 XP per minute) to discourage early quitting
                    var matchMinutes = state.CurrentTick / 10 / 60;
                    var baseXP = Math.Max(10, matchMinutes * 10); 

                    foreach (var playerEntry in state.Players)
                    {
                        var playerId = Guid.Parse(playerEntry.Key);
                        var isWinner = playerId.ToString() == winnerId;
                        
                        // Winners get a 50% XP bonus
                        var xpEarned = isWinner ? (int)(baseXP * 1.5) : baseXP;

                        // Upsert the player's season progress record
                        var progress = await db.Set<PlayerSeasonProgress>()
                            .FirstOrDefaultAsync(p => p.PlayerId == playerId && p.SeasonId == activeSeason.Id, stoppingToken);

                        if (progress == null)
                        {
                            progress = new PlayerSeasonProgress { PlayerId = playerId, SeasonId = activeSeason.Id, TotalXP = 0 };
                            db.Add(progress);
                        }

                        progress.TotalXP += xpEarned;
                    }
                }

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
                        if (playerIdStr == winnerId) 
                        {
                            profile.TotalWins++;
                            
                            var baseReward = 100;
                            if (await _featureManager.IsEnabledAsync("DoubleXPWeekend"))
                            {
                                baseReward *= 2;
                            }
                            
                            // Grant reward to player... (e.g. XP or Credits)
                            profile.PremiumCredits += baseReward;
                        }
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
                
                // Check if this was a tournament match (MatchId matches a TournamentMatch.Id)
                if (Guid.TryParse(state.MatchId, out var parsedMatchId))
                {
                    var tournamentMatch = await db.Set<TournamentMatch>()
                        .Include(m => m.Tournament)
                        .FirstOrDefaultAsync(m => m.Id == parsedMatchId, stoppingToken);

                    if (tournamentMatch != null && tournamentMatch.State == MatchState.InProgress)
                    {
                        tournamentMatch.WinnerId = Guid.Parse(winnerId);
                        tournamentMatch.State = MatchState.Finished;

                        // Advance the winner to the next round in the bracket
                        if (tournamentMatch.NextMatchId.HasValue)
                        {
                            var nextMatch = await db.Set<TournamentMatch>().FindAsync(new object[] { tournamentMatch.NextMatchId }, cancellationToken: stoppingToken);
                            
                            if (nextMatch != null)
                            {
                                // We don't know if they are Team A or Team B in the next match yet
                                if (nextMatch.TeamAId == null)
                                {
                                    nextMatch.TeamAId = tournamentMatch.WinnerId;
                                }
                                else
                                {
                                    nextMatch.TeamBId = tournamentMatch.WinnerId;
                                }
                            }
                        }
                        else
                        {
                            // No next match? This was the Grand Final!
                            if (tournamentMatch.Tournament != null)
                            {
                                tournamentMatch.Tournament.State = TournamentState.Completed;
                            }
                            
                            // Distribute the massive prize pool here...
                            Console.WriteLine($"[TOURNAMENT] Team {winnerId} has won the tournament!");
                        }
                    }
                    
                    await db.SaveChangesAsync(stoppingToken);
                }

                Console.WriteLine($"[DB] Match {state.MatchId} saved to PostgreSQL and Redis.");
            }
        }
    }
}
