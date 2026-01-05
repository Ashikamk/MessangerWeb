using MessangerWeb.Models;

namespace MessangerWeb.Services
{
    public class ChatService
    {
        public List<CombinedChatEntry> BuildCombinedChats(
            List<UserInfo> users,
            List<GroupInfo> groups,
            string selectedUserId = null,
            string selectedGroupId = null)
        {
            var combinedChats = new List<CombinedChatEntry>();

            // Add user chats
            foreach (var user in users)
            {
                combinedChats.Add(new CombinedChatEntry
                {
                    ChatType = "user",
                    ChatId = user.UserId,
                    ChatName = user.FullName,
                    PhotoBase64 = user.PhotoBase64,
                    LastMessageTime = user.LastMessageTime,
                    UnreadCount = user.UnreadCount,
                    IsSelected = selectedUserId == user.UserId
                });
            }

            // Add group chats
            foreach (var group in groups)
            {
                combinedChats.Add(new CombinedChatEntry
                {
                    ChatType = "group",
                    ChatId = group.GroupId.ToString(),
                    ChatName = group.GroupName,
                    PhotoBase64 = group.GroupImageBase64,
                    LastMessageTime = group.LastMessageTime,
                    UnreadCount = group.UnreadCount,
                    IsSelected = selectedGroupId == group.GroupId.ToString()
                });
            }

            // Sort by latest message time (newest first)
            return combinedChats
                .OrderByDescending(c => c.LastMessageTime)
                .ThenByDescending(c => c.UnreadCount) // Unread chats with same time appear first
                .ToList();
        }
    }
}