// File: Nebula.Client/Services/GameConnectionManager.cs
using Microsoft.AspNetCore.SignalR.Client;
using Blazored.LocalStorage;
using Nebula.Shared.Models;
using Nebula.Shared.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Nebula.Client.Services
{
    public class GameConnectionManager : IAsyncDisposable
    {
        private readonly HubConnection _hubConnection;
        private readonly ILocalStorageService _localStorage;
        private readonly NavigationManager _navManager;

        // C# Events that our UI components will subscribe to
        public event Action<string>? OnSystemMessageReceived;
        public event Action<GameState>? OnGameStateUpdated;
        public event Action<ChatMessage>? OnChatMessageReceived;
        public event Action<string>? OnMatchJoined;
        public event Action<NotificationPayload>? OnGlobalNotificationReceived;
        public event Action<GameState>? OnSpectatorTickReceived;

        // New gameplay events
        public event Action<string, int, decimal>? OnComboAlert;           // (playerId, streakCount, multiplier)
        public event Action<string, string>? OnPowerSurgeActivated;        // (playerId, playerName)
        public event Action<string, string>? OnMatchAlert;                  // (alertType, message)
        
        public string CurrentRegion { get; private set; } = "Unknown";
        public event Action? OnStateChanged;
        
        public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

        public GameConnectionManager(ILocalStorageService localStorage, NavigationManager navManager, IConfiguration configuration)
        {
            _localStorage = localStorage;
            _navManager = navManager;

            var apiBase = configuration["ApiBaseUrl"];
            if (string.IsNullOrWhiteSpace(apiBase))
                apiBase = "http://localhost:5168/";

            _hubConnection = new HubConnectionBuilder()
                .WithUrl($"{apiBase.TrimEnd('/')}/gamehub", options =>
                {
                    // Dynamically provide the JWT for authentication
                    options.AccessTokenProvider = async () => 
                        await _localStorage.GetItemAsync<string>("authToken");
                })
                .WithAutomaticReconnect() // Auto-retries if connection drops
                .Build();

            // Wire up the client-side listeners defined in IGameClient
            _hubConnection.On<string>(nameof(IGameClient.ReceiveSystemMessage), (message) =>
            {
                OnSystemMessageReceived?.Invoke(message);
            });

            _hubConnection.On<GameState>(nameof(IGameClient.ReceiveGameStateTick), (state) =>
            {
                OnGameStateUpdated?.Invoke(state);
            });

            _hubConnection.On<ChatMessage>(nameof(IGameClient.ReceiveChatMessage), (message) =>
            {
                OnChatMessageReceived?.Invoke(message);
            });

            _hubConnection.On<string>(nameof(IGameClient.MatchJoined), (matchId) =>
            {
                OnMatchJoined?.Invoke(matchId);
            });

            _hubConnection.On<NotificationPayload>(nameof(IGameClient.ReceiveGlobalNotification), (payload) =>
            {
                OnGlobalNotificationReceived?.Invoke(payload);
            });

            _hubConnection.On<string>(nameof(IGameClient.AcknowledgeRegion), (regionName) =>
            {
                Console.WriteLine($"[Network] Connected to Edge Node in: {regionName}");
                CurrentRegion = regionName;
                OnStateChanged?.Invoke();
            });

            _hubConnection.On<GameState>(nameof(IGameClient.ReceiveSpectatorTick), (state) =>
            {
                OnSpectatorTickReceived?.Invoke(state);
            });

            _hubConnection.On<string>(nameof(IGameClient.ForceDisconnect), async (reason) =>
            {
                Console.WriteLine($"[SECURITY] Evicted by server: {reason}");
                await _localStorage.RemoveItemAsync("authToken");
                await _hubConnection.StopAsync();
                _navManager.NavigateTo($"/suspended?reason={Uri.EscapeDataString(reason)}");
            });

            _hubConnection.On<string, int, decimal>(nameof(IGameClient.ReceiveComboAlert), (playerId, streak, mult) =>
            {
                OnComboAlert?.Invoke(playerId, streak, mult);
            });

            _hubConnection.On<string, string>(nameof(IGameClient.ReceivePowerSurgeActivated), (playerId, playerName) =>
            {
                OnPowerSurgeActivated?.Invoke(playerId, playerName);
            });

            _hubConnection.On<string, string>(nameof(IGameClient.ReceiveMatchAlert), (alertType, message) =>
            {
                OnMatchAlert?.Invoke(alertType, message);
            });
        }

        public async Task ConnectAsync()
        {
            if (_hubConnection.State == HubConnectionState.Disconnected)
            {
                await _hubConnection.StartAsync();
                OnStateChanged?.Invoke();
            }
        }

        public async Task DispatchDroneAsync(string targetResource)
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("DispatchDrone", targetResource);
            }
        }

        public async Task SellResourceAsync(string resource, double amount)
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("SellResource", resource, amount);
            }
        }

        public async Task LaunchSabotageAsync(string targetPlayerId, SabotageType sabotageType)
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("LaunchSabotage", targetPlayerId, sabotageType);
            }
        }

        public async Task BuildDefenseAsync(DefenseType type)
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("BuildDefense", type);
            }
        }

        public async Task DroneOverchargeAsync(string targetResource)
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("DroneOvercharge", targetResource);
            }
        }

        public async Task ActivatePowerSurgeAsync()
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("ActivatePowerSurge");
            }
        }

        public async Task EmergencySellAsync()
        {
            if (IsConnected)
            {
                await _hubConnection.InvokeAsync("EmergencySell");
            }
        }

        // Method for the UI to send commands to the server
        public async Task StartTutorialMatchAsync()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to the syndicate servers.");

            await _hubConnection.InvokeAsync("StartTutorialMatch");
        }

        public async Task JoinMatchQueue()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to the syndicate servers.");

            await _hubConnection.InvokeAsync("JoinMatchQueue");
        }

        public async Task EnterMatchAsync(string matchId)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to the syndicate servers.");

            await _hubConnection.InvokeAsync("EnterMatch", matchId);
        }

        public async Task StartWatchingMatchAsync(string matchId)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to the syndicate servers.");

            await _hubConnection.InvokeAsync("WatchMatch", matchId);
        }

        public async Task SendChatMessage(string message, string channel)
        {
            if (IsConnected)
            {
                await _hubConnection.SendAsync("SendChatMessage", message, channel);
            }
        }

        public async Task<string> GetCurrentPlayerIdAsync()
        {
            var token = await _localStorage.GetItemAsync<string>("authToken");
            if (string.IsNullOrEmpty(token)) return string.Empty;

            // Decode JWT payload (without validation, as server handles real security)
            var parts = token.Split('.');
            if (parts.Length < 2) return string.Empty;

            var jsonBytes = ParseBase64Url(parts[1]);
            using var document = JsonDocument.Parse(jsonBytes);
            if (document.RootElement.TryGetProperty("sub", out var subject))
                return subject.GetString() ?? string.Empty;

            return string.Empty;
        }

        private static byte[] ParseBase64Url(string base64)
        {
            base64 = base64.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }

        public async ValueTask DisposeAsync()
        {
            if (_hubConnection is not null)
            {
                await _hubConnection.DisposeAsync();
            }
        }
    }
}
