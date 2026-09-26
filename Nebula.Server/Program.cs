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
using Stripe;
using Stripe.Checkout;

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

// Add NotificationService for pushing Global Toasts
builder.Services.AddSingleton<NotificationService>();

// Add the Game Tick Server
builder.Services.AddHostedService<GameTickService>();

// Add PremiumCurrencyService
builder.Services.AddScoped<PremiumCurrencyService>();

StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

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
    
    // Auto-register if user doesn't exist (for prototype testing)
    if (user == null)
    {
        user = new PlayerProfile
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            PremiumCredits = 1000 // Give them some starting credits
        };
        db.Players.Add(user);
        await db.SaveChangesAsync();
    }
    // Verify password (using BCrypt for example)
    else if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
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

guildApi.MapPost("/vault/donate", async (DonateDto request, ClaimsPrincipal user, NebulaDbContext db, PremiumCurrencyService premiumService) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    
    var player = await db.Players.FindAsync(userId);
    if (player == null || player.GuildId == null) 
        return Results.BadRequest("Not in a syndicate.");

    var guild = await db.Guilds.FindAsync(player.GuildId);
    if (guild == null) 
        return Results.NotFound();

    if (request.Amount <= 0) 
        return Results.BadRequest("Invalid amount.");
        
    try
    {
        guild.VaultCredits += request.Amount;
        guild.Version = Guid.NewGuid();
        
        var success = await premiumService.AdjustBalanceAsync(userId, -request.Amount, TransactionType.GuildVaultDonation, guild.Id.ToString());
        
        if (!success)
            return Results.BadRequest("Insufficient funds or invalid transaction.");

        return Results.Ok(new { NewBalance = player.PremiumCredits, VaultTotal = guild.VaultCredits });
    }
    catch (Exception ex)
    {
        return Results.Conflict(ex.Message);
    }
});

var questApi = app.MapGroup("/api/quests").RequireAuthorization();

questApi.MapPost("/{questId}/claim", async (Guid questId, ClaimsPrincipal user, NebulaDbContext db, PremiumCurrencyService premiumService) =>
{
    var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    
    var quest = await db.Set<PlayerQuest>().FirstOrDefaultAsync(q => q.Id == questId && q.PlayerId == userId);
    
    if (quest == null) return Results.NotFound();
    if (!quest.IsCompleted) return Results.BadRequest("Quest not completed yet.");
    if (quest.IsClaimed) return Results.BadRequest("Reward already claimed.");

    try
    {
        quest.IsClaimed = true;
        
        var success = await premiumService.AdjustBalanceAsync(userId, quest.RewardCredits, TransactionType.QuestReward, quest.Id.ToString());
        
        if (!success)
            return Results.BadRequest("Failed to claim reward.");

        var player = await db.Players.FindAsync(userId);
        return Results.Ok(new { NewBalance = player?.PremiumCredits ?? 0 });
    }
    catch (Exception ex)
    {
        return Results.Conflict(ex.Message);
    }
});

var paymentsApi = app.MapGroup("/api/payments");

// 1. Endpoint to start the checkout process (Requires Auth)
paymentsApi.MapPost("/checkout", async (ClaimsPrincipal user) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    var options = new SessionCreateOptions
    {
        PaymentMethodTypes = new List<string> { "card" },
        LineItems = new List<SessionLineItemOptions>
        {
            new SessionLineItemOptions
            {
                PriceData = new SessionLineItemPriceDataOptions
                {
                    UnitAmount = 500, // $5.00 in cents
                    Currency = "usd",
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = "1,000 Syndicate Coins",
                    },
                },
                Quantity = 1,
            },
        },
        Mode = "payment",
        SuccessUrl = "https://localhost:5001/store?success=true",
        CancelUrl = "https://localhost:5001/store?canceled=true",
        // CRITICAL: We pass the UserId via metadata so the webhook knows who to credit
        Metadata = new Dictionary<string, string>
        {
            { "UserId", userId },
            { "CreditsAmount", "1000" } 
        }
    };

    var service = new SessionService();
    Session session = await service.CreateAsync(options);

    return Results.Ok(new { Url = session.Url });
}).RequireAuthorization();

// 2. Endpoint to listen for Stripe Webhooks (NO Auth - Stripe calls this directly)
paymentsApi.MapPost("/webhook", async (HttpRequest request, IConfiguration config, PremiumCurrencyService premiumService, NebulaDbContext db) =>
{
    var json = await new StreamReader(request.Body).ReadToEndAsync();
    var endpointSecret = config["Stripe:WebhookSecret"];

    try
    {
        // 1. Cryptographically verify the payload actually came from Stripe
        var stripeEvent = EventUtility.ConstructEvent(
            json,
            request.Headers["Stripe-Signature"],
            endpointSecret
        );

        if (stripeEvent.Type == "checkout.session.completed")
        {
            var session = stripeEvent.Data.Object as Session;
            
            var userId = Guid.Parse(session!.Metadata["UserId"]);
            var creditsAmount = int.Parse(session.Metadata["CreditsAmount"]);
            var sessionId = session.Id;

            // 2. IDEMPOTENCY CHECK: Did we already process this session?
            var alreadyProcessed = await db.Set<PremiumLedgerEntry>()
                .AnyAsync(l => l.ReferenceId == sessionId);

            if (!alreadyProcessed)
            {
                // 3. Grant the currency using our secure ledger service (Phase 31)
                await premiumService.AdjustBalanceAsync(
                    userId, 
                    creditsAmount, 
                    Nebula.Domain.Entities.TransactionType.RealMoneyPurchase, 
                    sessionId);
                    
                Console.WriteLine($"[Stripe] Successfully credited user {userId} with {creditsAmount} coins.");
            }
        }

        return Results.Ok();
    }
    catch (StripeException e)
    {
        Console.WriteLine($"[Stripe Webhook Error] {e.Message}");
        return Results.BadRequest();
    }
});

// Map the GameHub to a route
app.MapHub<Nebula.Server.Hubs.GameHub>("/gamehub");

app.Run();

// Define DTO inline for brevity
public record CreateGuildDto(string Name, string Tag);
public record DonateDto(int Amount);
