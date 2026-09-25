using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nebula.Client;
using Nebula.Client.Services;
using Blazored.LocalStorage;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register HTTP Client for API calls (like login)
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register LocalStorage and our Custom Connection Manager
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped<GameConnectionManager>();

await builder.Build().RunAsync();
