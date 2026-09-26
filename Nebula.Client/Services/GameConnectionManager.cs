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

        public async ValueTask DisposeAsync()
        {
            if (_hubConnection is not null)
            {
                await _hubConnection.DisposeAsync();
            }
        }
    }
}
