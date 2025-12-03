namespace MessangerWeb.Models
{
    public class VideoCallParticipantDto
    {
        public string ParticipantId { get; set; }
        public string CallId { get; set; }
        public int UserId { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string PhotoBase64 { get; set; }
        public DateTime? JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public string Status { get; set; }
        public int? Duration { get; set; }
        public bool IsCurrentUser { get; set; }
    }
}
