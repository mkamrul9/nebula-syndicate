using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nebula.Client;
using Nebula.Client.Services;
using Blazored.LocalStorage;
using System.Globalization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBase))
    apiBase = "http://localhost:5168/";
if (!apiBase.EndsWith('/'))
    apiBase += "/";

// Register LocalStorage and our Custom Connection Manager
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddScoped<AuthHeaderHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthHeaderHandler>();
    // WASM must use the browser fetch pipeline. A bare HttpClientHandler can hang the boot loader.
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiBase) };
});
builder.Services.AddScoped<GameConnectionManager>();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Do not call IJSRuntime before RunAsync. That waits forever and leaves the splash at 99%.
CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

await builder.Build().RunAsync();
