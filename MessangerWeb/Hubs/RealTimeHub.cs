// Hubs/RealTimeHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace MessangerWeb.Hubs
{
    public class RealTimeHub : Hub
    {
        private static readonly ConcurrentDictionary<string, string> _userConnections = new();
        private static readonly ConcurrentDictionary<int, List<string>> _groupMembers = new();
        private readonly ILogger<RealTimeHub> _logger;

        public RealTimeHub(ILogger<RealTimeHub> logger)
        {
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                _userConnections[userId] = Context.ConnectionId;

                // Join user-specific groups
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
                await Groups.AddToGroupAsync(Context.ConnectionId, $"chatlist_{userId}");

                _logger.LogInformation($"✅ User {userId} connected to RealTimeHub");
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                _userConnections.TryRemove(userId, out _);

                // Clean up from all groups
                foreach (var group in _groupMembers)
                {
                    if (group.Value.Contains(userId))
                    {
                        group.Value.Remove(userId);
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{group.Key}");
                    }
                }

                await base.OnDisconnectedAsync(exception);
            }
        }

        // Join a specific group chat
        public async Task JoinGroup(int groupId)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                // Track group membership
                if (!_groupMembers.ContainsKey(groupId))
                {
                    _groupMembers[groupId] = new List<string>();
                }

                if (!_groupMembers[groupId].Contains(userId))
                {
                    _groupMembers[groupId].Add(userId);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, $"group_{groupId}");
                _logger.LogInformation($"👥 User {userId} joined group {groupId}");
            }
        }

        // Leave a group
        public async Task LeaveGroup(int groupId)
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                if (_groupMembers.ContainsKey(groupId) && _groupMembers[groupId].Contains(userId))
                {
                    _groupMembers[groupId].Remove(userId);
                }

                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{groupId}");
                _logger.LogInformation($"👥 User {userId} left group {groupId}");
            }
        }

        // Get all online users
        public List<string> GetOnlineUsers()
        {
            return _userConnections.Keys.ToList();
        }

        // Get group members
        public List<string> GetGroupMembers(int groupId)
        {
            return _groupMembers.ContainsKey(groupId) ? _groupMembers[groupId] : new List<string>();
        }
    }
}