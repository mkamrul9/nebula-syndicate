using System.Text;
using Nebula.Server.Services;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Nebula.Server.Data;
using Nebula.Domain.Entities;
using Nebula.Shared.Models;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add PostgreSQL DbContext
builder.Services.AddDbContext<NebulaDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        // Tell EF Core where to output the migration files
        b => b.MigrationsAssembly("Nebula.Server") 
    ));

var redisConnection = builder.Configuration.GetConnectionString("Redis");
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection!));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };

        // THIS IS CRITICAL FOR SIGNALR:
        // WebSockets cannot pass headers, so we must read the token from the query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                // If the request is for our game hub...
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/gamehub"))
                {
                    // Read the token out of the query string
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add SignalR services
builder.Services.AddSignalR(); 

// Add the GameStateManager as a Singleton so we can hold the live state in RAM
builder.Services.AddSingleton<GameStateManager>();
builder.Services.AddSingleton<PlayerConnectionTracker>();

// Add the Background Service as a Singleton so the Hub can inject it
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MatchmakingService>());

// Add the Match Persister Service
builder.Services.AddSingleton<MatchPersisterService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MatchPersisterService>());

// Add the Game Tick Server
builder.Services.AddHostedService<GameTickService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.UseHttpsRedirection();

app.MapPost("/api/auth/login", async (LoginDto request, NebulaDbContext db, IConfiguration config) =>
{
    var user = await db.Players.SingleOrDefaultAsync(p => p.Username == request.Username);
    
    // Verify password (using BCrypt for example)
    if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    // Create claims
    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.UniqueName, user.Username)
    };

    // Generate JWT
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    
    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"],
        audience: config["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddDays(7), // Give them a week before logging out
        signingCredentials: creds
    );

    return Results.Ok(new { Token = new JwtSecurityTokenHandler().WriteToken(token) });
});

var api = app.MapGroup("/api/profile").RequireAuthorization();

api.MapGet("/stats", async (ClaimsPrincipal user, NebulaDbContext db) =>
{
    // Extract the UserId from the JWT token claims
    var userIdString = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userIdString == null) return Results.Unauthorized();
    
    var userId = Guid.Parse(userIdString);
    
    var profile = await db.Players
        .AsNoTracking() // Performance boost for read-only queries
        .FirstOrDefaultAsync(p => p.Id == userId);
        
    if (profile == null) return Results.NotFound();

    return Results.Ok(new PlayerStatsDto
    {
        Username = profile.Username,
        TotalMatchesPlayed = profile.TotalMatchesPlayed,
        TotalWins = profile.TotalWins,
        PremiumCredits = profile.PremiumCredits
    });
});

api.MapGet("/history", async (ClaimsPrincipal user, NebulaDbContext db, int page = 1, int pageSize = 10) =>
{
    var userIdString = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userIdString == null) return Results.Unauthorized();
    
    var userId = Guid.Parse(userIdString);
    
    var history = await db.MatchRecords
        .AsNoTracking()
        .Where(m => m.ParticipantIds.Contains(userId)) 
        .OrderByDescending(m => m.EndedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(m => new MatchHistoryItemDto
        {
            MatchId = m.Id.ToString(),
            IsVictory = m.WinnerId == userId,
            DurationSeconds = m.DurationInSeconds,
            EndedAt = m.EndedAt
        })
        .ToListAsync();

    return Results.Ok(history);
});

app.MapGet("/api/leaderboard", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    
    // Fetch the top 50 players (0 to 49) in descending order
    var topPlayers = await db.SortedSetRangeByRankWithScoresAsync("leaderboard:wins", 0, 49, Order.Descending);

    var result = topPlayers.Select((entry, index) => new
    {
        Rank = index + 1,
        Username = entry.Element.ToString(),
        Wins = (int)entry.Score
    });

    return Results.Ok(result);
});

app.MapGet("/api/replay/{matchId}", async (Guid matchId, NebulaDbContext db) =>
{
    var match = await db.MatchRecords
        .AsNoTracking()
        .FirstOrDefaultAsync(m => m.Id == matchId);

    if (match == null || string.IsNullOrEmpty(match.ReplayDataJson)) 
        return Results.NotFound();

    // Return the raw JSON string directly; minimal overhead
    return Results.Content(match.ReplayDataJson, "application/json");
});

var guildApi = app.MapGroup("/api/guilds").RequireAuthorization();

// Create a new Guild
guildApi.MapPost("/create", async (CreateGuildDto request, ClaimsPrincipal user, NebulaDbContext db) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    
    // 1. Verify player exists and isn't already in a guild
    var player = await db.Players.FindAsync(userId);
    if (player == null) return Results.NotFound();
    if (player.GuildId != null) return Results.BadRequest("You are already in a syndicate.");

    // 2. Verify name/tag uniqueness
    if (await db.Guilds.AnyAsync(g => g.Name == request.Name || g.Tag == request.Tag))
    {
        return Results.Conflict("Syndicate Name or Tag already claimed.");
    }

    // 3. Create the Guild
    var guild = new Guild
    {
        Id = Guid.NewGuid(),
        Name = request.Name,
        Tag = request.Tag,
        LeaderId = userId
    };

    db.Guilds.Add(guild);

    // 4. Update the player
    player.GuildId = guild.Id;
    player.Role = GuildRole.Leader;

    await db.SaveChangesAsync();

    return Results.Ok(new { GuildId = guild.Id, Name = guild.Name });
});

// Leave a Guild
guildApi.MapPost("/leave", async (ClaimsPrincipal user, NebulaDbContext db) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var player = await db.Players.FindAsync(userId);
    
    if (player == null || player.GuildId == null) return Results.BadRequest("Not in a syndicate.");
    if (player.Role == GuildRole.Leader) return Results.BadRequest("Leaders must pass leadership before leaving.");

    player.GuildId = null;
    player.Role = GuildRole.None;

    await db.SaveChangesAsync();
    return Results.Ok();
});

guildApi.MapPost("/vault/donate", async (DonateDto request, ClaimsPrincipal user, NebulaDbContext db) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    
    // 1. Begin a strict database transaction
    using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

    try
    {
        // 2. Fetch records
        var player = await db.Players.FindAsync(userId);
        if (player == null || player.GuildId == null) 
            return Results.BadRequest("Not in a syndicate.");

        var guild = await db.Guilds.FindAsync(player.GuildId);
        if (guild == null) 
            return Results.NotFound();

        // 3. Validate logic
        if (request.Amount <= 0) 
            return Results.BadRequest("Invalid amount.");
            
        if (player.PremiumCredits < request.Amount) 
            return Results.BadRequest("Insufficient funds.");

        // 4. Perform the transfer
        player.PremiumCredits -= request.Amount;
        guild.VaultCredits += request.Amount;

        // Update concurrency tokens so other simultaneous transactions fail
        player.Version = Guid.NewGuid();
        guild.Version = Guid.NewGuid();

        // 5. Save changes
        await db.SaveChangesAsync();
        
        // 6. Commit the transaction ONLY if SaveChangesAsync succeeded without concurrency exceptions
        await transaction.CommitAsync();

        return Results.Ok(new { NewBalance = player.PremiumCredits, VaultTotal = guild.VaultCredits });
    }
    catch (DbUpdateConcurrencyException)
    {
        // A hacker (or lag) tried to double-spend at the exact same millisecond.
        // The transaction automatically rolls back.
        await transaction.RollbackAsync();
        return Results.Conflict("Transaction collision detected. Please try again.");
    }
    catch (Exception)
    {
        await transaction.RollbackAsync();
        return Results.StatusCode(500);
    }
});

// Map the GameHub to a route
app.MapHub<Nebula.Server.Hubs.GameHub>("/gamehub");

app.Run();

// Define DTO inline for brevity
public record CreateGuildDto(string Name, string Tag);
public record DonateDto(int Amount);
