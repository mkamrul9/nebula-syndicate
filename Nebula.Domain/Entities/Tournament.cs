using System;
using System.Collections.Generic;

namespace Nebula.Domain.Entities
{
    public class Tournament
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int EntryFee { get; set; } // Paid from the Syndicate Vault
        public int MaxTeams { get; set; } // e.g., 16, 32, 64
        public TournamentState State { get; set; } = TournamentState.Registration;
        
        public DateTime StartTimeUTC { get; set; }
        
        public ICollection<TournamentMatch> Matches { get; set; } = new List<TournamentMatch>();
    }

    public class TournamentMatch
    {
        public Guid Id { get; set; }
        public Guid TournamentId { get; set; }
        
        public Tournament? Tournament { get; set; }
        public int RoundNumber { get; set; } // 1 (Quarter), 2 (Semi), 3 (Final)
        
        public Guid? TeamAId { get; set; }
        public Guid? TeamBId { get; set; }
        public Guid? WinnerId { get; set; }
        
        // The crucial link: The winner of this match goes to this next match
        public Guid? NextMatchId { get; set; } 
        
        public DateTime ScheduledStartTimeUTC { get; set; }
        public MatchState State { get; set; } = MatchState.WaitingForTeams;
    }

    public enum TournamentState { Registration, BracketGeneration, InProgress, Completed }
    public enum MatchState { WaitingForTeams, InProgress, Finished, Forfeit }
}
