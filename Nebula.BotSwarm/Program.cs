using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nebula.Shared.Models;

Console.WriteLine("Initiating Nebula Syndicate Bot Swarm...");

const int BotCount = 5000; // Start small, scale up
const string ServerUrl = "http://localhost:8080/gamehub"; // Assuming local docker or dev server

var bots = new List<Task>();

// Use Parallel execution to ramp up connections quickly
Parallel.For(0, BotCount, i =>
{
    bots.Add(RunBotAsync($"BotCommander_{i}"));
});

await Task.WhenAll(bots);

async Task RunBotAsync(string botName)
{
    // 1. Configure the Headless SignalR Client
    var connection = new HubConnectionBuilder()
        .WithUrl(ServerUrl, options =>
        {
            // Inject the backdoor token for Staging environments
            options.Headers.Add("X-Bot-Auth-Bypass", botName); 
        })
        .AddMessagePackProtocol() // Match the server's protocol
        .Build();

    // 2. Wire up the event listeners (just like the Blazor client)
    connection.On<GameState>("ReceiveGameStateTick", (state) =>
    {
        // Bots don't need to render a UI, they just consume the state to keep the socket active.
        // In an advanced bot, you could read the state to make "smart" decisions.
    });
    
    connection.On<string>("ReceiveSystemMessage", (message) => 
    {
        // Consume system messages
    });

    try
    {
        // 3. Connect to the server
        await connection.StartAsync();
        Console.WriteLine($"[Connected] {botName}");

        // 4. Enter Matchmaking
        await connection.InvokeAsync("JoinMatchQueue");

        var random = new Random();

        // 5. Simulate chaotic player behavior
        while (connection.State == HubConnectionState.Connected)
        {
            // Wait a random amount of time (1 to 5 seconds)
            await Task.Delay(random.Next(1000, 5000));

            // 70% chance to build an Ironium Drone, 30% for Plasma
            var target = random.NextDouble() > 0.3 ? "Ironium" : "Plasma";
            
            try
            {
                // Send the command blindly (Server anti-cheat from Phase 24 handles invalid actions)
                await connection.InvokeAsync("DispatchDrone", target);
            }
            catch 
            {
                // Ignore transient network errors during heavy load
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bot Death] {botName}: {ex.Message}");
    }
}
