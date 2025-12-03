namespace MessangerWeb.Models
{
    public class VideoCallDetails
    {
        public string CallId { get; set; }
        public int CallerId { get; set; }
        public string CallerName { get; set; }
        public string CallerPhotoBase64 { get; set; }
        public string ReceiverType { get; set; }
        public string ReceiverName { get; set; }
        public string CallType { get; set; }
        public string CallStatus { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? Duration { get; set; }
        public int ParticipantsCount { get; set; }
        public List<VideoCallParticipantDto> Participants { get; set; } = new List<VideoCallParticipantDto>();
    }
}
