// Hubs/ChatHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace MessangerWeb.Hubs
{
    public class ChatHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> _userConnections = new();
        private readonly ILogger<ChatHub> _logger;

        public ChatHub(ILogger<ChatHub> logger)
        {
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                _userConnections[userId] = Context.ConnectionId;

                // Add to user-specific group
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");

                // Add to chat list group for real-time updates
                await Groups.AddToGroupAsync(Context.ConnectionId, $"chatlist_{userId}");

                // Add to profile updates group
                await Groups.AddToGroupAsync(Context.ConnectionId, $"profile_updates_{userId}");

                // Notify others about online status
                await Clients.OthersInGroup($"chatlist_{userId}").SendAsync("UserOnlineStatus", userId, true);

                _logger.LogInformation($"✅ User {userId} connected. Connection ID: {Context.ConnectionId}");
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                _userConnections.TryRemove(userId, out _);

                // Remove from groups
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chatlist_{userId}");
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"profile_updates_{userId}");

                // Notify others about offline status
                await Clients.OthersInGroup($"chatlist_{userId}").SendAsync("UserOnlineStatus", userId, false);

                _logger.LogInformation($"❌ User {userId} disconnected.");
            }
            await base.OnDisconnectedAsync(exception);
        }

        // ========================================
        // REAL-TIME MESSAGE HANDLING
        // ========================================

        public async Task SendMessageToUser(string senderId, string receiverId, string message, DateTime sentAt)
        {
            // Send message to receiver
            await Clients.Group($"user_{receiverId}").SendAsync("ReceiveMessage", new
            {
                senderId = senderId,
                message = message,
                sentAt = sentAt,
                isNewMessage = true,
                updateChatOrder = true
            });

            // Send message back to sender (for their own UI)
            await Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", new
            {
                senderId = senderId,
                message = message,
                sentAt = sentAt,
                isNewMessage = true,
                updateChatOrder = true
            });

            // Update chat lists for both users
            await UpdateChatListForUser(senderId);
            await UpdateChatListForUser(receiverId);

            // Update unread counts
            await UpdateUnreadCounts(receiverId);
        }

        public async Task SendMessageToGroup(int groupId, string senderId, string message, string senderName, DateTime sentAt)
        {
            // Send message to all group members
            await Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", new
            {
                senderId = senderId,
                message = message,
                sentAt = sentAt,
                senderName = senderName,
                groupId = groupId,
                isNewMessage = true,
                updateChatOrder = true
            });

            // Update chat lists for all group members
            await UpdateChatListForGroupMembers(groupId);

            // Update unread counts for all group members except sender
            await UpdateGroupUnreadCounts(groupId, senderId);
        }

        // ========================================
        // REAL-TIME PROFILE UPDATES
        // ========================================

        public async Task BroadcastProfileUpdate(string userId, string firstName, string lastName, string photoBase64)
        {
            // Notify all users who have this user in their chat list
            await Clients.All.SendAsync("ProfileUpdated", new
            {
                userId = userId,
                firstName = firstName,
                lastName = lastName,
                fullName = $"{firstName} {lastName}",
                photoBase64 = photoBase64,
                updatedAt = DateTime.UtcNow
            });

            _logger.LogInformation($"📢 Profile update broadcast for user {userId}");
        }

        // ========================================
        // REAL-TIME GROUP UPDATES
        // ========================================

        public async Task BroadcastGroupUpdate(int groupId, string groupName, string groupImageBase64)
        {
            // Notify all group members
            await Clients.Group($"group_{groupId}").SendAsync("GroupUpdated", new
            {
                groupId = groupId,
                groupName = groupName,
                groupImageBase64 = groupImageBase64,
                updatedAt = DateTime.UtcNow
            });

            // Update chat lists for all group members
            await UpdateChatListForGroupMembers(groupId);

            _logger.LogInformation($"📢 Group update broadcast for group {groupId}");
        }

        public async Task BroadcastGroupCreated(int groupId, List<string> memberIds)
        {
            foreach (var memberId in memberIds)
            {
                // Add each member to the group
                await Groups.AddToGroupAsync(_userConnections.GetValueOrDefault(memberId), $"group_{groupId}");

                // Notify each member
                await Clients.Group($"chatlist_{memberId}").SendAsync("GroupCreated", new
                {
                    groupId = groupId,
                    addedAt = DateTime.UtcNow
                });
            }

            // Update chat lists for all members
            foreach (var memberId in memberIds)
            {
                await UpdateChatListForUser(memberId);
            }

            _logger.LogInformation($"📢 Group {groupId} created, notified {memberIds.Count} members");
        }

        public async Task BroadcastGroupMemberAdded(int groupId, string memberId)
        {
            // Add member to group
            var connectionId = _userConnections.GetValueOrDefault(memberId);
            if (!string.IsNullOrEmpty(connectionId))
            {
                await Groups.AddToGroupAsync(connectionId, $"group_{groupId}");
            }

            // Notify all group members
            await Clients.Group($"group_{groupId}").SendAsync("GroupMemberAdded", new
            {
                groupId = groupId,
                memberId = memberId,
                addedAt = DateTime.UtcNow
            });

            // Update chat lists
            await UpdateChatListForGroupMembers(groupId);
            await UpdateChatListForUser(memberId);

            _logger.LogInformation($"📢 Member {memberId} added to group {groupId}");
        }

        public async Task BroadcastGroupMemberRemoved(int groupId, string memberId)
        {
            // Remove member from group
            var connectionId = _userConnections.GetValueOrDefault(memberId);
            if (!string.IsNullOrEmpty(connectionId))
            {
                await Groups.RemoveFromGroupAsync(connectionId, $"group_{groupId}");
            }

            // Notify remaining group members
            await Clients.Group($"group_{groupId}").SendAsync("GroupMemberRemoved", new
            {
                groupId = groupId,
                memberId = memberId,
                removedAt = DateTime.UtcNow
            });

            // Update chat lists
            await UpdateChatListForGroupMembers(groupId);
            await UpdateChatListForUser(memberId);

            _logger.LogInformation($"📢 Member {memberId} removed from group {groupId}");
        }

        // ========================================
        // REAL-TIME UNREAD COUNTS UPDATES
        // ========================================

        public async Task UpdateUnreadCounts(string userId)
        {
            await Clients.Group($"chatlist_{userId}").SendAsync("UpdateUnreadCounts");
        }

        public async Task UpdateGroupUnreadCounts(int groupId, string excludeUserId = null)
        {
            // This would typically get all group members from database
            // For now, we'll broadcast to all in the group
            await Clients.Group($"group_{groupId}").SendAsync("UpdateGroupUnreadCounts", new
            {
                groupId = groupId,
                excludeUserId = excludeUserId
            });
        }

        // ========================================
        // HELPER METHODS
        // ========================================

        private async Task UpdateChatListForUser(string userId)
        {
            await Clients.Group($"chatlist_{userId}").SendAsync("UpdateChatList");
        }

        private async Task UpdateChatListForGroupMembers(int groupId)
        {
            // This would typically get all group members from database
            // For now, we'll broadcast to all in the group chat list
            await Clients.Group($"group_{groupId}").SendAsync("UpdateChatListForGroup", groupId);
        }

        public static string GetConnectionId(string userId)
        {
            return _userConnections.TryGetValue(userId, out var connectionId) ? connectionId : null;
        }

        public static bool IsUserOnline(string userId)
        {
            return _userConnections.ContainsKey(userId);
        }

        // ========================================
        // CLIENT-SIDE INVOCABLE METHODS
        // ========================================

        public async Task SendTypingIndicator(string receiverId, bool isTyping)
        {
            await Clients.Group($"user_{receiverId}").SendAsync("UserTyping", Context.UserIdentifier, isTyping);
        }

        public async Task JoinGroup(string groupId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group_{groupId}");
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group_updates_{groupId}");
        }

        public async Task LeaveGroup(string groupId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{groupId}");
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_updates_{groupId}");
        }

        public async Task MarkMessagesAsRead(string chatId, string chatType)
        {
            var userId = Context.UserIdentifier;
            await Clients.Group($"chatlist_{userId}").SendAsync("MessagesMarkedAsRead", new
            {
                chatId = chatId,
                chatType = chatType,
                userId = userId
            });
        }
    }
}