namespace MessangerWeb.Services
{
    public class UserService
    {
        private readonly Dictionary<string, DateTime> _userLastSeen = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _onlineUsers = new HashSet<string>();

        public bool IsUserOnline(string userId)
        {
            return _onlineUsers.Contains(userId);
        }

        public DateTime? GetUserLastSeen(string userId)
        {
            return _userLastSeen.TryGetValue(userId, out var lastSeen) ? lastSeen : null;
        }

        public void UpdateUserLastSeen(string userId)
        {
            _userLastSeen[userId] = DateTime.UtcNow;
        }

        public void MarkUserOnline(string userId)
        {
            _onlineUsers.Add(userId);
            _userLastSeen[userId] = DateTime.UtcNow;
        }

        public void MarkUserOffline(string userId)
        {
            _onlineUsers.Remove(userId);
        }

    }
}
