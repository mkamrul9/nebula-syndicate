using Microsoft.EntityFrameworkCore;
using Nebula.Server.Data;
using Nebula.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula.Server.Services
{
    public class TournamentOrchestrator : BackgroundService
    {
        private readonly IServiceProvider _services;

        public TournamentOrchestrator(IServiceProvider services)
        {
            _services = services;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1)); // Check every minute

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NebulaDbContext>();

                // Find all matches that were supposed to start 5+ minutes ago but haven't
                var deadline = DateTime.UtcNow.AddMinutes(-5);
                var delinquentMatches = await db.Set<TournamentMatch>()
                    .Include(m => m.Tournament)
                    .Where(m => m.State == MatchState.WaitingForTeams 
                             && m.ScheduledStartTimeUTC < deadline
                             && m.TeamAId != null 
                             && m.TeamBId != null)
                    .ToListAsync(stoppingToken);

                foreach (var match in delinquentMatches)
                {
                    // Check our Redis/GameStateManager to see who actually clicked "Ready"
                    // (Assuming we have a method to check queued players)
                    var teamAIsReady = CheckIfTeamQueued(match.TeamAId.Value);
                    var teamBIsReady = CheckIfTeamQueued(match.TeamBId.Value);

                    if (teamAIsReady && !teamBIsReady)
                    {
                        match.WinnerId = match.TeamAId;
                        match.State = MatchState.Forfeit;
                        AdvanceWinner(db, match);
                    }
                    else if (teamBIsReady && !teamAIsReady)
                    {
                        match.WinnerId = match.TeamBId;
                        match.State = MatchState.Forfeit;
                        AdvanceWinner(db, match);
                    }
                    else if (!teamAIsReady && !teamBIsReady)
                    {
                        // Double forfeit - incredibly rare, but handle it (e.g., random winner or double elim)
                        match.State = MatchState.Forfeit;
                    }
                }

                if (delinquentMatches.Any())
                {
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
        }

        private void AdvanceWinner(NebulaDbContext db, TournamentMatch tournamentMatch)
        {
            // Advance the winner to the next round in the bracket
            if (tournamentMatch.NextMatchId.HasValue)
            {
                var nextMatch = db.Set<TournamentMatch>().Find(tournamentMatch.NextMatchId);
                if (nextMatch != null)
                {
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
                if (tournamentMatch.Tournament != null)
                {
                    tournamentMatch.Tournament.State = TournamentState.Completed;
                    Console.WriteLine($"[TOURNAMENT] Team {tournamentMatch.WinnerId} has won the tournament by forfeit!");
                }
            }
        }
        
        private bool CheckIfTeamQueued(Guid teamId) { return true; /* Implementation omitted */ }
    }
}
