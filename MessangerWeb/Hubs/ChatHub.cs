// Hubs/ChatHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace MessangerWeb.Hubs
{
    public class ChatHub : Hub
    {
        // Track online users
        private static readonly ConcurrentDictionary<string, string> _userConnections = new();

        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                _userConnections[userId] = Context.ConnectionId;

                // Add to user-specific group
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");

                // Add to chat list group
                await Groups.AddToGroupAsync(Context.ConnectionId, $"chatlist_{userId}");

                // Notify others about online status
                await Clients.Others.SendAsync("UserOnlineStatus", userId, true);

                Console.WriteLine($"✅ User {userId} connected. Connection ID: {Context.ConnectionId}");
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

                // Notify others about offline status
                await Clients.Others.SendAsync("UserOnlineStatus", userId, false);

                Console.WriteLine($"❌ User {userId} disconnected.");
            }
            await base.OnDisconnectedAsync(exception);
        }

        // New: Get user's connection ID
        public static string GetConnectionId(string userId)
        {
            return _userConnections.TryGetValue(userId, out var connectionId) ? connectionId : null;
        }

        // New: Check if user is online
        public static bool IsUserOnline(string userId)
        {
            return _userConnections.ContainsKey(userId);
        }

        // New: Send typing indicator
        public async Task SendTypingIndicator(string receiverId, bool isTyping)
        {
            await Clients.Group($"user_{receiverId}").SendAsync("UserTyping", Context.UserIdentifier, isTyping);
        }

        // New: Subscribe to group updates
        public async Task SubscribeToGroup(string groupId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group_{groupId}");
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group_updates_{groupId}");
            Console.WriteLine($"✅ User subscribed to group {groupId}");
        }

        // New: Unsubscribe from group
        public async Task UnsubscribeFromGroup(string groupId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{groupId}");
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_updates_{groupId}");
        }

        // New: Update user's chat list
        public async Task UpdateUserChatList(string userId)
        {
            await Clients.Group($"chatlist_{userId}").SendAsync("RefreshChatList");
        }

        // Keep existing methods for backward compatibility
        public async Task JoinChatList(string userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chatlist_{userId}");
        }

        public async Task LeaveChatList(string userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chatlist_{userId}");
        }

        public async Task JoinUserChat(string userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }

        public async Task LeaveUserChat(string userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
        }

        public async Task JoinGroupChat(string groupId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group_{groupId}");
        }

        public async Task LeaveGroupChat(string groupId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{groupId}");
        }
    }
}