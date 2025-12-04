namespace MessangerWeb.Models
{
    public class VideoCall
    {
        public string CallId { get; set; }
        public int CallerId { get; set; }
        public string ReceiverId { get; set; }
        public string ReceiverType { get; set; }
        public string CallType { get; set; }
        public string CallStatus { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? Duration { get; set; }
        public int ParticipantsCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
