using MessangerWeb.Models;

namespace WebsiteApplication.Services
{
    public class NotificationService
    {
        private readonly List<CallNotification> _callNotifications = new List<CallNotification>();
        private int _nextId = 1;

        public bool SaveCallNotification(CallNotificationRequest request)
        {
            try
            {
                var notification = new CallNotification
                {
                    Id = _nextId++,
                    ReceiverId = request.ReceiverId,
                    CallerId = request.CallerId,
                    CallerName = request.CallerName,
                    CallerPhoto = request.CallerPhoto,
                    CallType = request.CallType,
                    Timestamp = request.Timestamp,
                    IsRead = false
                };

                _callNotifications.Add(notification);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<CallNotification> GetPendingCallNotifications(string userId)
        {
            return _callNotifications
                .Where(n => n.ReceiverId == userId && !n.IsRead)
                .OrderByDescending(n => n.Timestamp)
                .ToList();
        }

        public void MarkNotificationAsRead(int notificationId)
        {
            var notification = _callNotifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null)
            {
                notification.IsRead = true;
            }
        }
    }

    public class CallNotification
    {
        public int Id { get; set; }
        public string ReceiverId { get; set; }
        public string CallerId { get; set; }
        public string CallerName { get; set; }
        public string CallerPhoto { get; set; }
        public string CallType { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsRead { get; set; }
    }
}
