// File: Nebula.Client/Services/GameConnectionManager.cs
using Microsoft.AspNetCore.SignalR.Client;
using Blazored.LocalStorage;
using Nebula.Shared.Models;
using Nebula.Shared.Interfaces;

namespace Nebula.Client.Services
{
    public class GameConnectionManager : IAsyncDisposable
    {
        private readonly HubConnection _hubConnection;
        private readonly ILocalStorageService _localStorage;

        // C# Events that our UI components will subscribe to
        public event Action<string>? OnSystemMessageReceived;
        public event Action<GameState>? OnGameStateUpdated;
        public event Action<ChatMessage>? OnChatMessageReceived;
        public event Action<string>? OnMatchJoined;
        
        public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

        public GameConnectionManager(ILocalStorageService localStorage)
        {
            _localStorage = localStorage;

            _hubConnection = new HubConnectionBuilder()
                // Assuming the server is running on the same domain/port for now
                .WithUrl("https://localhost:7001/gamehub", options =>
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
        }

        public async Task ConnectAsync()
        {
            if (_hubConnection.State == HubConnectionState.Disconnected)
            {
                await _hubConnection.StartAsync();
            }
        }

        // Method for the UI to send commands to the server
        public async Task JoinMatchQueue()
        {
            if (IsConnected)
            {
                await _hubConnection.SendAsync("JoinMatchQueue");
            }
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
            var payload = token.Split('.')[1];
            var jsonBytes = ParseBase64WithoutPadding(payload);
            var keyValuePairs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);
            
            // "sub" is the standard JWT claim for Subject/UserId
            return keyValuePairs?["sub"]?.ToString() ?? string.Empty;
        }

        private byte[] ParseBase64WithoutPadding(string base64)
        {
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
