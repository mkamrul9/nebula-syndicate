// File: Nebula.Shared/Models/NotificationPayload.cs
namespace Nebula.Shared.Models
{
    public class NotificationPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
    }

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Alert
    }
}
