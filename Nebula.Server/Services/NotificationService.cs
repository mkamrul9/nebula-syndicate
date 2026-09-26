// File: Nebula.Server/Services/NotificationService.cs
using Microsoft.AspNetCore.SignalR;
using Nebula.Server.Hubs;
using Nebula.Shared.Interfaces;
using Nebula.Shared.Models;

namespace Nebula.Server.Services
{
    public class NotificationService
    {
        private readonly IHubContext<GameHub, IGameClient> _hubContext;

        public NotificationService(IHubContext<GameHub, IGameClient> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task NotifyUserAsync(string userId, string title, string message, NotificationType type = NotificationType.Info)
        {
            var payload = new NotificationPayload
            {
                Title = title,
                Message = message,
                Type = type
            };

            // SignalR naturally routes to all connections authenticated with this UserId
            await _hubContext.Clients.User(userId).ReceiveGlobalNotification(payload);
        }
    }
}
