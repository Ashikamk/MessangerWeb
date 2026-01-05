namespace MessangerWeb.Models
{
    public class UserDashboardViewModel
    {
        public string FullName { get; set; }
        public string UserId { get; set; }
        public string UserEmail { get; set; }
        public string UserPhotoBase64 { get; set; }
        public List<UserInfo> Users { get; set; }
        public UserInfo SelectedUser { get; set; }
        public List<ChatMessage> Messages { get; set; }

        // Add these properties for groups
        public List<GroupInfo> Groups { get; set; }
        public GroupInfo SelectedGroup { get; set; }
        public List<GroupMessage> GroupMessages { get; set; }

        // Add this to track current view type
        public string CurrentViewType { get; set; } // "user" or "group"
        public List<CombinedChatEntry> CombinedChats { get; set; }
    }

    public class UserInfo
    {
        public string UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public byte[] PhotoData { get; set; }
        public DateTime LastMessageTime { get; set; } // Make sure this exists
        public int UnreadCount { get; set; }

        // Add last message preview
        public string LastMessagePreview { get; set; }

        public string PhotoBase64
        {
            get
            {
                if (PhotoData != null && PhotoData.Length > 0)
                {
                    return Convert.ToBase64String(PhotoData);
                }
                return null;
            }
        }
    }

    public class ChatMessage
    {
        public int MessageId { get; set; }
        public string SenderEmail { get; set; }
        public string ReceiverEmail { get; set; }
        public string SenderName { get; set; }
        public string ReceiverName { get; set; }
        public string MessageText { get; set; }
        public DateTime SentAt { get; set; }
        public bool IsRead { get; set; }
        public bool IsCurrentUserSender { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileOriginalName { get; set; }
        public string ImagePath { get; set; }
        public long FileSize { get; set; }
        public string SenderPhotoBase64 { get; set; }
        public string ReceiverPhotoBase64 { get; set; }

        // Add call message properties
        public bool IsCallMessage { get; set; }
        public string CallDuration { get; set; }
        public string CallStatus { get; set; }

        // Add these missing properties that JavaScript expects
        public bool HasFile => !string.IsNullOrEmpty(FilePath) || !string.IsNullOrEmpty(ImagePath);
        public bool IsImage => !string.IsNullOrEmpty(ImagePath) ||
                              (!string.IsNullOrEmpty(FileOriginalName) &&
                               new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }
                               .Contains(Path.GetExtension(FileOriginalName)?.ToLower()));
    }

    // Profile update model
    public class ProfileUpdateModel
    {
        public string UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public IFormFile ProfileImage { get; set; }
    }

    // Group related models
    public class GroupInfo
    {
        public int GroupId { get; set; }
        public string GroupName { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public byte[] GroupImage { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime LastMessageTime { get; set; } // Make sure this exists
        public int UnreadCount { get; set; }
        public int MemberCount { get; set; }

        public string GroupImageBase64 => GroupImage != null ? Convert.ToBase64String(GroupImage) : null;
    }

    public class GroupMember
    {
        public int GroupMemberId { get; set; }
        public int GroupId { get; set; }
        public string StudentEmail { get; set; }
        public DateTime JoinedAt { get; set; }
        public string StudentName { get; set; }
    }

    public class GroupMessage
    {
        public int MessageId { get; set; }
        public int GroupId { get; set; }
        public string SenderEmail { get; set; }
        public string SenderName { get; set; }
        public string MessageText { get; set; }
        public string MessageRtf { get; set; }
        public string ImagePath { get; set; }
        public string FilePath { get; set; }
        public string FileOriginalName { get; set; }
        public long FileSize { get; set; }
        public DateTime SentAt { get; set; }
        public bool IsRead { get; set; }
        public bool IsCurrentUserSender { get; set; }
        public string SenderPhotoBase64 { get; set; }

        // Add call message properties
        public bool IsCallMessage { get; set; }
        public string CallDuration { get; set; }
        public string CallStatus { get; set; }

        // Add these missing properties that JavaScript expects
        public bool HasFile => !string.IsNullOrEmpty(FilePath) || !string.IsNullOrEmpty(ImagePath);
        public bool IsImage => !string.IsNullOrEmpty(ImagePath) ||
                              (!string.IsNullOrEmpty(FileOriginalName) &&
                               new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }
                               .Contains(Path.GetExtension(FileOriginalName)?.ToLower()));
    }

    public class CombinedChatEntry
    {
        public string ChatType { get; set; } // "user" or "group"
        public string ChatId { get; set; }
        public string ChatName { get; set; }
        public string PhotoBase64 { get; set; }
        public DateTime LastMessageTime { get; set; } // Use consistent name
        public int UnreadCount { get; set; }
        public DateTime LastTime { get; set; }
        public bool IsSelected { get; set; }
        public string LastMessagePreview { get; set; } // Optional: for showing last message
        public bool HasUnreadMessages => UnreadCount > 0;
    }

    public class UserViewModel
    {
        public string UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string PhotoBase64 { get; set; }
        public bool IsGroup { get; set; }
    }

    public class CreateGroupModel
    {
        public string GroupName { get; set; }
        public List<string> SelectedMembers { get; set; } = new List<string>();
        public IFormFile GroupImage { get; set; }
    }

    // Add these new models for video call functionality
    public class SaveCallDurationRequest
    {
        public string CallId { get; set; }
        public string ReceiverId { get; set; }
        public string GroupId { get; set; }
        public int Duration { get; set; }
        public string FormattedDuration { get; set; }
        public string CallType { get; set; }
        public bool IsInitiator { get; set; } = true;
    }

    public class UpdateGroupModel
    {
        public int GroupId { get; set; }
        public string GroupName { get; set; }
        public IFormFile GroupImage { get; set; }
    }

    public class AddMembersModel
    {
        public int GroupId { get; set; }
        public List<string> MemberEmails { get; set; }
    }

    // Video Call History Models
    public class VideoCallHistory
    {
        public string CallId { get; set; }
        public string CallerId { get; set; }
        public string CallerName { get; set; }
        public string ReceiverId { get; set; }
        public string ReceiverName { get; set; }
        public string ReceiverType { get; set; } // "User" or "Group"
        public string CallType { get; set; } // "Video" or "Audio"
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int? Duration { get; set; }
        public string Status { get; set; } // "Initiated", "Accepted", "Rejected", "Completed", "Failed", "Missed"
        public int ParticipantsCount { get; set; }
    }

    public class VideoCallParticipant
    {
        public int ParticipantId { get; set; }
        public string CallId { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; }
        public string Status { get; set; } // "Invited", "Joined", "Left", "Declined"
        public DateTime? JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public int Duration { get; set; }
    }

    // Request Models for Video Calls
    public class StartCallRequest
    {
        public string ReceiverId { get; set; }
        public string ReceiverType { get; set; } // "User" or "Group"
    }

    public class UpdateCallStatusRequest
    {
        public string CallId { get; set; }
        public string Status { get; set; } // "Initiated", "Accepted", "Rejected", "Completed", "Failed", "Missed"
    }

    public class EndCallRequest
    {
        public string CallId { get; set; }
        public string Status { get; set; } = "Completed";
    }

    public class UpdateParticipantsRequest
    {
        public string CallId { get; set; }
        public int ParticipantsCount { get; set; }
    }

    public class AddCallParticipantRequest
    {
        public string CallId { get; set; }
        public int UserId { get; set; }
        public string Status { get; set; }
    }

    public class UpdateCallParticipantStatusRequest
    {
        public string CallId { get; set; }
        public int UserId { get; set; }
        public string Status { get; set; }
        public string JoinedAt { get; set; }
    }

    public class UpdateCallParticipantDurationRequest
    {
        public string CallId { get; set; }
        public int UserId { get; set; }
        public int Duration { get; set; }
    }

    public class CallParticipantInfo
    {
        public string UserName { get; set; }
        public int Duration { get; set; }
        public string FormattedDuration { get; set; }
    }
}
