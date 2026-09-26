using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nebula.Client;
using Nebula.Client.Services;
using Blazored.LocalStorage;
using Microsoft.JSInterop;
using System.Globalization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register HTTP Client for API calls (like login)
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register LocalStorage and our Custom Connection Manager
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped<GameConnectionManager>();

// 1. Register Localization Services
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

var host = builder.Build();

// 2. Read the user's saved language preference via JS Interop before rendering UI
var jsInterop = host.Services.GetRequiredService<IJSRuntime>();
var result = await jsInterop.InvokeAsync<string>("localStorage.getItem", "preferredLanguage");

// 3. Apply the Culture
var cultureName = !string.IsNullOrWhiteSpace(result) ? result : "en-US";
var culture = new CultureInfo(cultureName);

CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

await host.RunAsync();
