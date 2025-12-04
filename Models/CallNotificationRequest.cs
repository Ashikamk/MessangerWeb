namespace MessangerWeb.Models
{
    public class CallNotificationRequest
    {
        public string ReceiverId { get; set; }
        public string CallerId { get; set; }
        public string CallerName { get; set; }
        public string CallerPhoto { get; set; }
        public string CallType { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
