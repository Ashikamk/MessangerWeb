using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MessangerWeb.Models;
using MessangerWeb.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Npgsql;
using Microsoft.Extensions.Configuration;
using MessangerWeb.Hubs;

namespace MessangerWeb.Controllers
{
    public class UserDashboardController : Controller
    {
        private readonly string connectionString;
        private readonly string fileUploadPath = "wwwroot/uploads/chatfiles/";
        private readonly IVideoCallHistoryService _videoCallHistoryService;
        private readonly ILogger<UserDashboardController> _logger;
        private readonly IVideoCallParticipantService _videoCallParticipantService;
        private readonly IHubContext<ChatHub> _chatHub; // Changed from _hubContext to _chatHub for consistency

        public UserDashboardController(
            IVideoCallHistoryService videoCallHistoryService,
            IVideoCallParticipantService videoCallParticipantService,
            ILogger<UserDashboardController> logger,
            IConfiguration configuration,
            IHubContext<ChatHub> chatHub) // Removed duplicate parameter
        {
            _videoCallHistoryService = videoCallHistoryService;
            _videoCallParticipantService = videoCallParticipantService;
            _logger = logger;
            connectionString = configuration.GetConnectionString("DefaultConnection");
            _chatHub = chatHub; // Now using _chatHub consistently
        }
        public IActionResult Index(string selectedUserId = null, int? selectedGroupId = null)
        {
            var userType = HttpContext.Session.GetString("UserType");
            var userId = HttpContext.Session.GetString("UserId");
            var userEmail = HttpContext.Session.GetString("Email");

            if (userType != "User" || string.IsNullOrEmpty(userId))
            {
                TempData["ErrorMessage"] = "Please login to access the dashboard.";
                return RedirectToAction("UserLogin", "Account");
            }

            if (!IsUserActive(userId))
            {
                HttpContext.Session.Clear();
                TempData["ErrorMessage"] = "Your account has been deactivated.";
                return RedirectToAction("UserLogin", "Account");
            }

            var firstName = HttpContext.Session.GetString("FirstName");
            var lastName = HttpContext.Session.GetString("LastName");
            var currentUser = GetUserById(userId);

            // Get users and groups
            var users = GetAllUsersWithLastMessage(userId, userEmail);
            var groups = GetUserGroups(userEmail);

            // Get combined chats
            var combinedChats = GetUnifiedChatsInternal(userId, userEmail, selectedUserId, selectedGroupId?.ToString());


            var model = new UserDashboardViewModel
            {
                FullName = $"{firstName} {lastName}".Trim(),
                UserId = userId,
                UserEmail = userEmail,
                UserPhotoBase64 = currentUser?.PhotoBase64,
                Users = users,
                Groups = groups,
                CombinedChats = combinedChats,
                CurrentViewType = !string.IsNullOrEmpty(selectedUserId) ? "user" :
                                 selectedGroupId.HasValue ? "group" : null
            };

            // MARK AS READ WHEN OPENING CHAT
            if (!string.IsNullOrEmpty(selectedUserId))
            {
                model.SelectedUser = GetUserById(selectedUserId);
                if (model.SelectedUser != null)
                {
                    model.Messages = GetMessages(userEmail, model.SelectedUser.Email);
                    model.CurrentViewType = "user";

                    // Mark messages as read immediately
                    MarkMessagesAsRead(userEmail, model.SelectedUser.Email);

                    // Update the chat's unread count in combined chats
                    var userChat = combinedChats.FirstOrDefault(c =>
                        c.ChatType == "user" && c.ChatId == selectedUserId);
                    if (userChat != null)
                    {
                        userChat.UnreadCount = 0;
                        userChat.IsSelected = true;
                    }
                }
            }
            else if (selectedGroupId.HasValue)
            {
                model.SelectedGroup = model.Groups.FirstOrDefault(g => g.GroupId == selectedGroupId.Value);
                if (model.SelectedGroup != null)
                {
                    model.GroupMessages = GetGroupMessagesByGroupId(selectedGroupId.Value, userEmail);
                    model.CurrentViewType = "group";

                    // Mark group messages as read immediately
                    MarkGroupMessagesAsReadForUser(userEmail, selectedGroupId.Value);

                    // Update the chat's unread count in combined chats
                    var groupChat = combinedChats.FirstOrDefault(c =>
                        c.ChatType == "group" && c.ChatId == selectedGroupId.Value.ToString());
                    if (groupChat != null)
                    {
                        groupChat.UnreadCount = 0;
                        groupChat.IsSelected = true;
                    }
                }
            }

            return View(model);
        }

        // Real-time broadcasting methods
        private async Task BroadcastMessageSent(string senderId, string receiverId, string message, DateTime sentAt)
        {
            await _chatHub.Clients.Group($"user_{receiverId}").SendAsync("ReceiveMessage", new
            {
                senderId = senderId,
                message = message,
                sentAt = sentAt,
                isNewMessage = true,
                updateChatOrder = true
            });
        }

        private async Task BroadcastGroupMessageSent(int groupId, string senderId, string message, string senderName, DateTime sentAt)
        {
            await _chatHub.Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", new
            {
                senderId = senderId,
                message = message,
                sentAt = sentAt,
                senderName = senderName,
                groupId = groupId,
                isNewMessage = true,
                updateChatOrder = true
            });
        }

        private async Task BroadcastProfileUpdate(string userId, string firstName, string lastName, string photoBase64)
        {
            await _chatHub.Clients.All.SendAsync("ProfileUpdated", new
            {
                userId = userId,
                firstName = firstName,
                lastName = lastName,
                fullName = $"{firstName} {lastName}",
                photoBase64 = photoBase64,
                updatedAt = DateTime.UtcNow
            });
        }

        private async Task BroadcastGroupUpdate(int groupId, string groupName, string groupImageBase64)
        {
            await _chatHub.Clients.Group($"group_{groupId}").SendAsync("GroupUpdated", new
            {
                groupId = groupId,
                groupName = groupName,
                groupImageBase64 = groupImageBase64,
                updatedAt = DateTime.UtcNow
            });
        }

        private async Task UpdateChatListForUser(string userId)
        {
            await _chatHub.Clients.Group($"chatlist_{userId}").SendAsync("UpdateChatList");
        }

        private async Task UpdateUnreadCounts(string userId)
        {
            await _chatHub.Clients.Group($"chatlist_{userId}").SendAsync("UpdateUnreadCounts");
        }

        private async Task UpdateChatOrderAfterMessage(string currentUserId, string currentUserEmail,
    string receiverId = null, int? groupId = null)
        {
            try
            {
                // Update the user's chat list
                var users = GetAllUsersWithLastMessage(currentUserId, currentUserEmail);
                var groups = GetUserGroups(currentUserEmail);

                var chatService = new ChatService();
                var updatedChats = chatService.BuildCombinedChats(users, groups);

                // Broadcast update via SignalR
                await _chatHub.Clients.Group($"chatlist_{currentUserId}").SendAsync("UpdateChatList", updatedChats);

                // If it's a group message, update all group members
                if (groupId.HasValue)
                {
                    var groupMembers = await GetGroupMemberIdsAsync(groupId.Value);
                    foreach (var memberId in groupMembers)
                    {
                        if (memberId != currentUserId)
                        {
                            await _chatHub.Clients.Group($"chatlist_{memberId}").SendAsync("UpdateChatList");
                        }
                    }
                }
                // If it's a user message, update the receiver too
                else if (!string.IsNullOrEmpty(receiverId))
                {
                    await _chatHub.Clients.Group($"chatlist_{receiverId}").SendAsync("UpdateChatList");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating chat order: {ex.Message}");
            }
        }
        private List<CombinedChatEntry> BuildCombinedChats(
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
                    LastTime = user.LastMessageTime,
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
                    LastTime = group.LastMessageTime,
                    LastMessageTime = group.LastMessageTime,
                    UnreadCount = group.UnreadCount,
                    IsSelected = selectedGroupId == group.GroupId.ToString()
                });
            }

            // Sort by last activity time (newest first), then by unread count
            return combinedChats
                .OrderByDescending(c => c.LastTime)
                .ThenByDescending(c => c.UnreadCount > 0) // Unread chats first
                .ThenBy(c => c.ChatName)
                .ToList();
        }

        private void UpdateCombinedChatsAfterSelection(List<CombinedChatEntry> combinedChats,
            string chatType, string chatId)
        {
            var chat = combinedChats.FirstOrDefault(c =>
                c.ChatType == chatType && c.ChatId == chatId);

            if (chat != null)
            {
                // Clear unread count when selected
                chat.UnreadCount = 0;
                chat.IsSelected = true;

                // Move to top (update last time to current time)
                chat.LastTime = DateTime.UtcNow;

                // Re-sort
                combinedChats.Sort((a, b) =>
                {
                    // Sort by LastTime descending
                    var timeCompare = b.LastTime.CompareTo(a.LastTime);
                    if (timeCompare != 0) return timeCompare;

                    // Then by unread count
                    return b.UnreadCount.CompareTo(a.UnreadCount);
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MarkChatAsRead(string chatType, string chatId)
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                var userId = HttpContext.Session.GetString("UserId");

                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false, message = "User not logged in" });
                }

                bool result = false;

                if (chatType == "user")
                {
                    // Get user email from userId
                    var user = GetUserById(chatId);
                    if (user != null)
                    {
                        result = MarkMessagesAsRead(userEmail, user.Email);
                    }
                }
                else if (chatType == "group")
                {
                    if (int.TryParse(chatId, out int groupId))
                    {
                        result = MarkGroupMessagesAsReadForUser(userEmail, groupId);
                    }
                }

                if (result)
                {
                    // Get updated chat list for the current user
                    var users = GetAllUsersWithLastMessage(userId, userEmail);
                    var groups = GetUserGroups(userEmail);
                    var chatService = new ChatService();
                    var updatedChats = chatService.BuildCombinedChats(users, groups);

                    // Broadcast update to self
                    await _chatHub.Clients.Group($"chatlist_{userId}").SendAsync("UpdateChatList", updatedChats);

                    return Json(new
                    {
                        success = true,
                        chats = updatedChats
                    });
                }

                return Json(new { success = false, message = "Failed to mark messages as read" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error marking chat as read: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpGet]
        public IActionResult GetUpdatedChatList()
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                var userEmail = HttpContext.Session.GetString("Email");

                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false, message = "User not authenticated" });
                }

                var users = GetAllUsersWithLastMessage(userId, userEmail);
                var groups = GetUserGroups(userEmail);
                var chatService = new ChatService();
                var chats = chatService.BuildCombinedChats(users, groups);

                return Json(new
                {
                    success = true,
                    chats = chats,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(string receiverId, string messageText)
        {
            var senderId = HttpContext.Session.GetString("UserId");
            var senderEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(senderId))
            {
                return Json(new { success = false, message = "Session expired. Please login again." });
            }

            try
            {
                var receiverUser = GetUserById(receiverId);
                if (receiverUser == null)
                {
                    return Json(new { success = false, message = "Receiver not found" });
                }

                var utcNow = DateTime.UtcNow;

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    var query = @"INSERT INTO messages (sender_email, receiver_email, message, sent_at, is_read) 
                         VALUES (@SenderEmail, @ReceiverEmail, @Message, @SentAt, 0)";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@ReceiverEmail", receiverUser.Email);
                        command.Parameters.AddWithValue("@Message", messageText);
                        command.Parameters.AddWithValue("@SentAt", utcNow);
                        await command.ExecuteNonQueryAsync();
                    }
                }

                // REAL-TIME BROADCAST
                // 1. Send message to receiver
                await BroadcastMessageSent(senderId, receiverId, messageText, utcNow);

                // 2. Send message back to sender (for their own UI)
                await _chatHub.Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", new
                {
                    senderId = senderId,
                    message = messageText,
                    sentAt = utcNow,
                    isNewMessage = true,
                    updateChatOrder = true
                });

                // 3. Update chat lists for both users
                await UpdateChatListForUser(senderId);
                await UpdateChatListForUser(receiverId);

                // 4. Update unread counts
                await UpdateUnreadCounts(receiverId);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }



        [HttpPost]
        public async Task<IActionResult> AddCallParticipant([FromBody] AddCallParticipantRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                    return Json(new { success = false, message = "User not authenticated" });

                var success = await _videoCallParticipantService.AddParticipantAsync(
                    request.CallId, request.UserId, request.Status);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding call participant");
                return Json(new { success = false, message = "Error adding participant" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCallParticipantStatus([FromBody] UpdateCallParticipantStatusRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                    return Json(new { success = false, message = "User not authenticated" });

                DateTime? joinedAt = null;
                if (!string.IsNullOrEmpty(request.JoinedAt))
                {
                    joinedAt = DateTime.Parse(request.JoinedAt);
                }

                var success = await _videoCallParticipantService.UpdateParticipantStatusAsync(
                    request.CallId, request.UserId, request.Status, joinedAt);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating call participant status");
                return Json(new { success = false, message = "Error updating participant status" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCallParticipantDuration([FromBody] UpdateCallParticipantDurationRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                    return Json(new { success = false, message = "User not authenticated" });

                var success = await _videoCallParticipantService.UpdateParticipantDurationAsync(
                    request.CallId, request.UserId, request.Duration);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating call participant duration");
                return Json(new { success = false, message = "Error updating participant duration" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCallDetails(string callId)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                    return Json(new { success = false, message = "User not authenticated" });

                var callDetails = await _videoCallParticipantService.GetCallDetailsAsync(callId);
                return Json(new { success = true, callDetails = callDetails });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting call details");
                return Json(new { success = false, message = "Error getting call details" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetDetailedCallHistory()
        {
            try
            {
                var userIdStr = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
                    return Json(new { success = false, message = "User not authenticated" });

                var callHistory = await _videoCallParticipantService.GetUserCallHistoryAsync(userId);
                return Json(new { success = true, callHistory = callHistory });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting detailed call history");
                return Json(new { success = false, message = "Error getting call history" });
            }
        }


        [HttpPost]
        public async Task<IActionResult> SendFile(IFormFile file, string receiverId)
        {
            var senderId = HttpContext.Session.GetString("UserId");
            var senderEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(senderId))
            {
                return Json(new { success = false, message = "Session expired. Please login again." });
            }

            if (string.IsNullOrEmpty(receiverId))
            {
                return Json(new { success = false, message = "Invalid receiver ID." });
            }

            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Invalid file data." });
            }

            try
            {
                var receiverUser = GetUserById(receiverId);
                if (receiverUser == null)
                {
                    return Json(new { success = false, message = "Receiver not found" });
                }

                if (!Directory.Exists(fileUploadPath))
                {
                    Directory.CreateDirectory(fileUploadPath);
                }

                var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                var filePath = Path.Combine(fileUploadPath, fileName);
                var relativePath = $"/uploads/chatfiles/{fileName}";

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
                var isImage = imageExtensions.Contains(Path.GetExtension(file.FileName).ToLower());

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    string query;

                    if (isImage)
                    {
                        query = @"INSERT INTO messages (sender_email, receiver_email, message, sent_at, is_read, image_path, file_name, file_original_name) 
                                 VALUES (@SenderEmail, @ReceiverEmail, '', NOW(), 0, @ImagePath, @FileName, @FileOriginalName)";
                    }
                    else
                    {
                        query = @"INSERT INTO messages (sender_email, receiver_email, message, sent_at, is_read, file_path, file_name, file_original_name) 
                                 VALUES (@SenderEmail, @ReceiverEmail, '', NOW(), 0, @FilePath, @FileName, @FileOriginalName)";
                    }

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@ReceiverEmail", receiverUser.Email);
                        command.Parameters.AddWithValue("@FilePath", (object)(isImage ? null : relativePath) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@ImagePath", (object)(isImage ? relativePath : null) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@FileName", fileName);
                        command.Parameters.AddWithValue("@FileOriginalName", file.FileName);
                        command.ExecuteNonQuery();
                    }
                }

                // Broadcast via SignalR
                var messageData = new
                {
                    senderId = senderId,
                    message = "",
                    sentAt = DateTime.UtcNow,
                    filePath = isImage ? null : relativePath,
                    imagePath = isImage ? relativePath : null,
                    fileName = file.FileName,
                    fileOriginalName = file.FileName,
                    receiverId = receiverId
                };

                await _chatHub.Clients.Group($"user_{receiverId}").SendAsync("ReceiveMessage", messageData);
                await _chatHub.Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", messageData);

                return Json(new { success = true, fileName = file.FileName, isImage = isImage });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpGet]
        public IActionResult GetMessages(string otherUserId)
        {
            var currentUserId = HttpContext.Session.GetString("UserId");
            var currentUserEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(currentUserId) || string.IsNullOrEmpty(otherUserId))
            {
                return Json(new { success = false, message = "Invalid user data" });
            }

            var otherUser = GetUserById(otherUserId);
            if (otherUser == null)
            {
                return Json(new { success = false, message = "User not found" });
            }

            var messages = GetMessages(currentUserEmail, otherUser.Email);
            return Json(new { success = true, messages = messages });
        }

        private List<ChatMessage> GetMessages(string currentUserEmail, string otherUserEmail)
        {
            var messages = new List<ChatMessage>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = @"SELECT m.*, 
                         s1.firstname as sender_firstname, s1.lastname as sender_lastname,
                         s2.firstname as receiver_firstname, s2.lastname as receiver_lastname
                         FROM messages m
                         LEFT JOIN students s1 ON m.sender_email = s1.email
                         LEFT JOIN students s2 ON m.receiver_email = s2.email
                         WHERE (m.sender_email = @CurrentUserEmail AND m.receiver_email = @OtherUserEmail)
                         OR (m.sender_email = @OtherUserEmail AND m.receiver_email = @CurrentUserEmail)
                         ORDER BY m.sent_at ASC";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@CurrentUserEmail", currentUserEmail);
                        command.Parameters.AddWithValue("@OtherUserEmail", otherUserEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var message = new ChatMessage
                                {
                                    MessageId = Convert.ToInt32(reader["id"]),
                                    SenderEmail = reader["sender_email"].ToString(),
                                    ReceiverEmail = reader["receiver_email"].ToString(),
                                    SenderName = $"{reader["sender_firstname"]} {reader["sender_lastname"]}",
                                    ReceiverName = $"{reader["receiver_firstname"]} {reader["receiver_lastname"]}",
                                    MessageText = reader["message"]?.ToString() ?? "",
                                    SentAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["sent_at"]), DateTimeKind.Utc),
                                    IsRead = Convert.ToBoolean(reader["is_read"]),
                                    IsCurrentUserSender = reader["sender_email"].ToString() == currentUserEmail,
                                    FilePath = reader["file_path"]?.ToString(),
                                    ImagePath = reader["image_path"]?.ToString(),
                                    FileName = reader["file_name"]?.ToString(),
                                    FileOriginalName = reader["file_original_name"]?.ToString(),
                                    // Add call message fields
                                    IsCallMessage = reader["is_call_message"] != DBNull.Value && Convert.ToBoolean(reader["is_call_message"]),
                                    CallDuration = reader["call_duration"]?.ToString(),
                                    CallStatus = reader["call_status"]?.ToString()
                                };
                                messages.Add(message);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching messages: {ex.Message}");
            }

            return messages;
        }

        private UserInfo GetUserById(string userId)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = "SELECT \"id\", \"firstname\", \"lastname\", \"email\", \"photo\" FROM students WHERE \"id\" = @UserId";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        if (int.TryParse(userId, out int id))
                        {
                            command.Parameters.AddWithValue("@UserId", id);
                        }
                        else
                        {
                            return null;
                        }

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                byte[] photoData = null;
                                if (reader["photo"] != DBNull.Value)
                                {
                                    photoData = (byte[])reader["photo"];
                                }

                                return new UserInfo
                                {
                                    UserId = reader["id"].ToString(),
                                    FirstName = reader["firstname"].ToString(),
                                    LastName = reader["lastname"].ToString(),
                                    FullName = $"{reader["firstname"]} {reader["lastname"]}",
                                    Email = reader["email"].ToString(),
                                    PhotoData = photoData
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching user: {ex.Message}");
            }

            return null;
        }

        private List<UserInfo> GetAllUsers(string currentUserId)
        {
            var users = new List<UserInfo>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    string query = "SELECT \"id\", \"firstname\", \"lastname\", \"email\", \"status\", \"photo\" FROM students WHERE \"status\" = 'Active' AND \"id\" != @CurrentUserId";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        if (int.TryParse(currentUserId, out int id))
                        {
                            command.Parameters.AddWithValue("@CurrentUserId", id);
                        }
                        else
                        {
                            return users;
                        }

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string userId = reader["id"].ToString();
                                string firstName = reader["firstname"].ToString();
                                string lastName = reader["lastname"].ToString();
                                string email = reader["email"].ToString();

                                byte[] photoData = null;
                                if (reader["photo"] != DBNull.Value)
                                {
                                    photoData = (byte[])reader["photo"];
                                }

                                users.Add(new UserInfo
                                {
                                    UserId = userId,
                                    FirstName = firstName,
                                    LastName = lastName,
                                    FullName = $"{firstName} {lastName}",
                                    Email = email,
                                    PhotoData = photoData
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching users: {ex.Message}");
            }

            return users;
        }

        private bool IsUserActive(string userId)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = "SELECT COUNT(*) FROM students WHERE \"id\" = @UserId AND \"status\" = 'Active'";
                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        if (int.TryParse(userId, out int id))
                        {
                            command.Parameters.AddWithValue("@UserId", id);
                            long result = (long)command.ExecuteScalar();
                            return result > 0;
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking user status: {ex.Message}");
                return false;
            }
        }

        [HttpGet]
        public IActionResult GetCurrentUserProfile()
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                var user = GetUserById(userId);
                if (user != null)
                {
                    return Json(new
                    {
                        success = true,
                        userId = user.UserId,
                        firstName = user.FirstName,
                        lastName = user.LastName,
                        photoBase64 = user.PhotoBase64
                    });
                }
                return Json(new { success = false, message = "User not found" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateProfile(ProfileUpdateModel model)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId) || userId != model.UserId)
            {
                return Json(new { success = false, message = "Unauthorized" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    string query;
                    NpgsqlCommand command;
                    string photoBase64 = null;

                    if (model.ProfileImage != null && model.ProfileImage.Length > 0)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            model.ProfileImage.CopyTo(memoryStream);
                            var photoData = memoryStream.ToArray();
                            photoBase64 = Convert.ToBase64String(photoData);

                            query = "UPDATE students SET \"firstname\" = @FirstName, \"lastname\" = @LastName, \"photo\" = @Photo WHERE \"id\" = @UserId";
                            command = new NpgsqlCommand(query, connection);
                            command.Parameters.AddWithValue("@FirstName", model.FirstName);
                            command.Parameters.AddWithValue("@LastName", model.LastName);
                            command.Parameters.AddWithValue("@Photo", photoData);
                            command.Parameters.AddWithValue("@UserId", int.Parse(model.UserId));
                        }
                    }
                    else
                    {
                        query = "UPDATE students SET \"firstname\" = @FirstName, \"lastname\" = @LastName WHERE \"id\" = @UserId";
                        command = new NpgsqlCommand(query, connection);
                        command.Parameters.AddWithValue("@FirstName", model.FirstName);
                        command.Parameters.AddWithValue("@LastName", model.LastName);
                        command.Parameters.AddWithValue("@UserId", int.Parse(model.UserId));
                    }

                    int rowsAffected = command.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        HttpContext.Session.SetString("FirstName", model.FirstName);
                        HttpContext.Session.SetString("LastName", model.LastName);

                        // REAL-TIME BROADCAST
                        await BroadcastProfileUpdate(userId, model.FirstName, model.LastName, photoBase64);

                        return Json(new { success = true, message = "Profile updated successfully", photoBase64 = photoBase64 });
                    }
                    else
                    {
                        return Json(new { success = false, message = "Failed to update profile" });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult GetGroups()
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                var groups = GetUserGroups(userEmail);
                return Json(new { success = true, groups = groups });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public IActionResult CreateGroup(CreateGroupModel model)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            var userName = HttpContext.Session.GetString("FirstName") + " " + HttpContext.Session.GetString("LastName");

            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            if (string.IsNullOrEmpty(model.GroupName) || model.SelectedMembers == null || !model.SelectedMembers.Any())
            {
                return Json(new { success = false, message = "Group name and at least one member are required" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    var groupQuery = @"INSERT INTO ""groups"" (""group_name"", ""created_by"", ""created_at"", ""group_image"", ""updated_at"", ""last_activity"") 
                             VALUES (@GroupName, @CreatedBy, NOW(), @GroupImage, NOW(), NOW()) RETURNING ""group_id"";";

                    int groupId;
                    using (var command = new NpgsqlCommand(groupQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupName", model.GroupName);
                        command.Parameters.AddWithValue("@CreatedBy", userEmail);

                        if (model.GroupImage != null && model.GroupImage.Length > 0)
                        {
                            using (var memoryStream = new MemoryStream())
                            {
                                model.GroupImage.CopyTo(memoryStream);
                                command.Parameters.AddWithValue("@GroupImage", memoryStream.ToArray());
                            }
                        }
                        else
                        {
                            command.Parameters.AddWithValue("@GroupImage", DBNull.Value);
                        }

                        groupId = Convert.ToInt32(command.ExecuteScalar());
                    }

                    var memberQuery = @"INSERT INTO ""group_members"" (""group_id"", ""student_email"", ""joined_at"") 
                              VALUES (@GroupId, @StudentEmail, NOW())";

                    // Add current user as member
                    using (var command = new NpgsqlCommand(memberQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@StudentEmail", userEmail);
                        command.ExecuteNonQuery();
                    }

                    // Add selected members
                    foreach (var memberEmail in model.SelectedMembers)
                    {
                        using (var command = new NpgsqlCommand(memberQuery, connection))
                        {
                            command.Parameters.AddWithValue("@GroupId", groupId);
                            command.Parameters.AddWithValue("@StudentEmail", memberEmail);
                            command.ExecuteNonQuery();
                        }
                    }

                    // Add creation message - set is_read to 1 for creation messages
                    var messageQuery = @"INSERT INTO ""group_messages"" (""group_id"", ""sender_email"", ""message"", ""sent_at"", ""is_read"") 
                               VALUES (@GroupId, @SenderEmail, @Message, NOW(), 1)";

                    using (var command = new NpgsqlCommand(messageQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@SenderEmail", userEmail);
                        command.Parameters.AddWithValue("@Message", $"{userName} created group '{model.GroupName}'");
                        command.ExecuteNonQuery();
                    }

                    return Json(new { success = true, message = "Group created successfully", groupId = groupId });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating group: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        [HttpGet]
        public IActionResult GetGroupMessages(int groupId)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                var messages = GetGroupMessagesByGroupId(groupId, userEmail);
                return Json(new { success = true, messages = messages });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendGroupMessage(int groupId, string messageText)
        {
            var senderEmail = HttpContext.Session.GetString("Email");
            var senderId = HttpContext.Session.GetString("UserId");
            var userName = HttpContext.Session.GetString("FirstName") + " " + HttpContext.Session.GetString("LastName");

            if (string.IsNullOrEmpty(senderEmail))
            {
                return Json(new { success = false, message = "Session expired. Please login again." });
            }

            if (groupId <= 0)
            {
                return Json(new { success = false, message = "Invalid group ID." });
            }

            if (string.IsNullOrEmpty(messageText))
            {
                return Json(new { success = false, message = "Message cannot be empty." });
            }

            try
            {
                var utcNow = DateTime.UtcNow;

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    var query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read) 
                                 VALUES (@GroupId, @SenderEmail, @Message, @SentAt, 0);
                                 UPDATE ""groups"" SET last_activity = @SentAt WHERE group_id = @GroupId;";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@Message", messageText);
                        command.Parameters.AddWithValue("@SentAt", utcNow);
                        await command.ExecuteNonQueryAsync();
                    }
                }

                // Broadcast via SignalR
                var messageData = new
                {
                    senderId = senderId,
                    message = messageText,
                    sentAt = utcNow,
                    senderName = userName,
                    groupId = groupId,
                    isNewMessage = true,
                    updateChatOrder = true
                };

                await _chatHub.Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", messageData);

                // Get all group members to update their chat lists
                var groupMembers = await GetGroupMemberIdsAsync(groupId);
                foreach (var memberId in groupMembers)
                {
                    if (memberId != senderId)
                    {
                        await _chatHub.Clients.Group($"chatlist_{memberId}").SendAsync("UpdateChatList");
                    }
                }
                // Update chat order after sending group message
                await UpdateChatOrderAfterMessage(senderId, senderEmail, null, groupId);
                await _chatHub.Clients.Group($"chatlist_{senderId}").SendAsync("UpdateChatList");

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        private async Task<List<string>> GetGroupMemberIdsAsync(int groupId)
        {
            var memberIds = new List<string>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    var query = @"SELECT s.id 
                                 FROM group_members gm
                                 INNER JOIN students s ON gm.student_email = s.email
                                 WHERE gm.group_id = @GroupId";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                memberIds.Add(reader["id"].ToString());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting group members: {ex.Message}");
            }

            return memberIds;
        }

        private List<string> GetGroupMemberIds(int groupId)
        {
            var memberIds = new List<string>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = @"SELECT s.id 
                         FROM group_members gm
                         INNER JOIN students s ON gm.student_email = s.email
                         WHERE gm.group_id = @GroupId";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                memberIds.Add(reader["id"].ToString());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting group members: {ex.Message}");
            }

            return memberIds;
        }

        [HttpGet]
        public IActionResult RefreshChatList()
        {
            var userId = HttpContext.Session.GetString("UserId");
            var userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not authenticated" });
            }

            try
            {
                var combinedChats = GetUnifiedChatsInternal(userId, userEmail, null, null);


                return Json(new
                {
                    success = true,
                    chats = combinedChats,
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpPost]
        public async Task<IActionResult> SendGroupFile(IFormFile file, int groupId)
        {
            var senderEmail = HttpContext.Session.GetString("Email");
            var senderId = HttpContext.Session.GetString("UserId");
            var userName = HttpContext.Session.GetString("FirstName") + " " + HttpContext.Session.GetString("LastName");

            if (string.IsNullOrEmpty(senderEmail))
            {
                return Json(new { success = false, message = "Session expired. Please login again." });
            }

            if (groupId <= 0)
            {
                return Json(new { success = false, message = "Invalid group ID." });
            }

            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Invalid file data." });
            }

            try
            {
                if (!Directory.Exists(fileUploadPath))
                {
                    Directory.CreateDirectory(fileUploadPath);
                }

                var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                var filePath = Path.Combine(fileUploadPath, fileName);
                var relativePath = $"/uploads/chatfiles/{fileName}";

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
                var isImage = imageExtensions.Contains(Path.GetExtension(file.FileName).ToLower());

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    string query;

                    if (isImage)
                    {
                        query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read, image_path, file_original_name) 
                         VALUES (@GroupId, @SenderEmail, '', NOW(), 0, @ImagePath, @FileOriginalName);
                         UPDATE ""groups"" SET last_activity = NOW() WHERE group_id = @GroupId;";
                    }
                    else
                    {
                        query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read, file_path, file_original_name) 
                         VALUES (@GroupId, @SenderEmail, '', NOW(), 0, @FilePath, @FileOriginalName);
                         UPDATE ""groups"" SET last_activity = NOW() WHERE group_id = @GroupId;";
                    }

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@FilePath", (object)(isImage ? null : relativePath) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@ImagePath", (object)(isImage ? relativePath : null) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@FileOriginalName", file.FileName);
                        command.ExecuteNonQuery();
                    }
                }

                // Broadcast via SignalR
                var messageData = new
                {
                    senderId = senderId,
                    message = "",
                    sentAt = DateTime.UtcNow,
                    senderName = userName,
                    filePath = isImage ? null : relativePath,
                    imagePath = isImage ? relativePath : null,
                    fileName = file.FileName,
                    fileOriginalName = file.FileName,
                    groupId = groupId
                };

                await _chatHub.Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", messageData);

                return Json(new { success = true, fileName = file.FileName, isImage = isImage });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpGet]
        public IActionResult GetUnreadCounts()
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false, unreadCounts = new List<object>() });
                }

                var unreadDict = GetUnreadMessagesCount(userEmail);

                // Convert to list format expected by frontend
                var unreadList = new List<object>();
                foreach (var kvp in unreadDict)
                {
                    string chatType = kvp.Key.StartsWith("user_") ? "user" : "group";
                    string chatId = kvp.Key.Replace("user_", "").Replace("group_", "");

                    unreadList.Add(new
                    {
                        type = chatType,
                        id = chatId,
                        count = kvp.Value
                    });
                }

                return Json(new { success = true, unreadCounts = unreadList });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting unread counts: {ex.Message}");
                return Json(new { success = false, unreadCounts = new List<object>() });
            }
        }

        private bool IsGroupId(int id)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    using (var cmd = new NpgsqlCommand("SELECT count(*) FROM \"groups\" WHERE group_id = @id", connection))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                    }
                }
            }
            catch { return false; }
        }

        private Dictionary<string, int> GetUnreadMessagesCount(string userEmail)
        {
            var unreadCounts = new Dictionary<string, int>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Ensure tracking table exists
                    EnsureGroupMessageReadStatusTableExists(connection);

                    // 1. Get unread individual messages
                    var individualQuery = @"
                SELECT s.id as user_id, COUNT(*) as unread_count
                FROM messages m
                INNER JOIN students s ON m.sender_email = s.email
                WHERE m.receiver_email = @UserEmail 
                AND m.is_read = 0
                AND m.sender_email != @UserEmail
                GROUP BY s.id";

                    using (var command = new NpgsqlCommand(individualQuery, connection))
                    {
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var userId = reader["user_id"].ToString();
                                var count = Convert.ToInt32(reader["unread_count"]);
                                // Add 'user_' prefix to distinguish from groups
                                unreadCounts[$"user_{userId}"] = count;
                            }
                        }
                    }

                    // 2. Get unread group messages - FIXED VERSION
                    var groupQuery = @"
                SELECT g.group_id, COUNT(*) as unread_count 
                FROM group_messages gm
                INNER JOIN ""groups"" g ON gm.group_id = g.group_id 
                INNER JOIN group_members gm2 ON g.group_id = gm2.group_id 
                WHERE gm2.student_email = @UserEmail 
                AND gm.sender_email != @UserEmail 
                AND NOT EXISTS (
                    SELECT 1 FROM group_message_read_status gmrs 
                    WHERE gmrs.group_message_id = gm.id 
                    AND gmrs.user_email = @UserEmail 
                    AND gmrs.has_read = TRUE
                ) 
                GROUP BY g.group_id";

                    using (var command = new NpgsqlCommand(groupQuery, connection))
                    {
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var groupId = reader["group_id"].ToString();
                                var count = Convert.ToInt32(reader["unread_count"]);
                                // Add 'group_' prefix to distinguish from users
                                unreadCounts[$"group_{groupId}"] = count;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetUnreadMessagesCount: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            return unreadCounts;
        }

        private List<GroupInfo> GetUserGroups(string userEmail)
        {
            var groups = new List<GroupInfo>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    EnsureGroupMessagesTableExists(connection);
                    EnsureGroupMessageReadStatusTableExists(connection);


                    var query = @"SELECT g.*, 
                     (SELECT COUNT(*) FROM group_members gm WHERE gm.group_id = g.group_id) as member_count,
                     (SELECT COUNT(*) 
                      FROM group_messages gm_msg 
                      WHERE gm_msg.group_id = g.group_id 
                      AND TRIM(LOWER(gm_msg.sender_email)) != TRIM(LOWER(@UserEmail))
                      AND NOT EXISTS (
                          SELECT 1 FROM group_message_read_status gmrs 
                          WHERE gmrs.group_message_id = gm_msg.id 
                          AND TRIM(LOWER(gmrs.user_email)) = TRIM(LOWER(@UserEmail))
                          AND gmrs.has_read = TRUE
                      )) as unread_count,
                     COALESCE(
                         (SELECT MAX(sent_at) 
                          FROM group_messages 
                          WHERE group_id = g.group_id), 
                         g.last_activity
                     ) as last_message_time
                     FROM ""groups"" g
                     INNER JOIN group_members gm ON g.group_id = gm.group_id
                     WHERE TRIM(LOWER(gm.student_email)) = TRIM(LOWER(@UserEmail))
                     ORDER BY last_message_time DESC NULLS LAST, g.group_name ASC";



                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            var count = 0;
                            while (reader.Read())
                            {
                                count++;
                                byte[] groupImage = null;
                                if (reader["group_image"] != DBNull.Value)
                                {
                                    groupImage = (byte[])reader["group_image"];
                                }

                                DateTime lastMessageTime = reader["last_message_time"] != DBNull.Value ?
                                    Convert.ToDateTime(reader["last_message_time"]) : DateTime.MinValue;

                                var g = new GroupInfo
                                {
                                    GroupId = Convert.ToInt32(reader["group_id"]),
                                    GroupName = reader["group_name"].ToString(),
                                    CreatedBy = reader["created_by"].ToString(),
                                    CreatedAt = Convert.ToDateTime(reader["created_at"]),
                                    GroupImage = groupImage,
                                    UpdatedAt = Convert.ToDateTime(reader["updated_at"]),
                                    LastActivity = Convert.ToDateTime(reader["last_activity"]),
                                    LastMessageTime = lastMessageTime,
                                    MemberCount = Convert.ToInt32(reader["member_count"]),
                                    UnreadCount = Convert.ToInt32(reader["unread_count"])
                                };
                                groups.Add(g);
                                Console.WriteLine($"[GetUserGroups] Found group: {g.GroupName} (ID: {g.GroupId}) for user: {userEmail}");
                            }
                            Console.WriteLine($"[GetUserGroups] Total groups found for {userEmail}: {count}");
                        }
                    }
                }
            }
            catch (NpgsqlException nex)
            {
                Console.WriteLine($"[GetUserGroups] DATABASE ERROR: {nex.Message}");
                Console.WriteLine($"[GetUserGroups] SQL State: {nex.SqlState}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetUserGroups] GENERAL ERROR: {ex.Message}");
                Console.WriteLine($"[GetUserGroups] Stack: {ex.StackTrace}");
            }

            return groups;
        }

        private List<GroupMessage> GetGroupMessagesByGroupId(int groupId, string currentUserEmail)
        {
            var messages = new List<GroupMessage>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    EnsureGroupMessageReadStatusTableExists(connection);

                    var query = @"SELECT gm.*, 
                         CONCAT(s.firstname, ' ', s.lastname) as sender_name,
                         EXISTS (
                             SELECT 1 FROM group_message_read_status gmrs 
                             WHERE gmrs.group_message_id = gm.id 
                             AND gmrs.user_email = @CurrentUserEmail
                             AND gmrs.has_read = TRUE
                         ) as is_read_by_current_user
                         FROM group_messages gm
                         LEFT JOIN students s ON gm.sender_email = s.email
                         WHERE gm.group_id = @GroupId
                         ORDER BY gm.sent_at ASC";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@CurrentUserEmail", currentUserEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                bool isReadByCurrentUser = Convert.ToBoolean(reader["is_read_by_current_user"]);

                                // Safely get optional file path columns
                                string imagePath = null;
                                string filePath = null;
                                string fileOriginalName = null;

                                try { imagePath = reader["image_path"]?.ToString(); } catch { }
                                try { filePath = reader["file_path"]?.ToString(); } catch { }
                                try { fileOriginalName = reader["file_original_name"]?.ToString(); } catch { }

                                messages.Add(new GroupMessage
                                {
                                    MessageId = Convert.ToInt32(reader["id"]),
                                    GroupId = Convert.ToInt32(reader["group_id"]),
                                    SenderEmail = reader["sender_email"].ToString(),
                                    SenderName = reader["sender_name"].ToString(),
                                    MessageText = reader["message"]?.ToString() ?? "",
                                    ImagePath = imagePath,
                                    FilePath = filePath,
                                    FileOriginalName = fileOriginalName,
                                    SentAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["sent_at"]), DateTimeKind.Utc),
                                    IsRead = isReadByCurrentUser,
                                    IsCurrentUserSender = reader["sender_email"].ToString() == currentUserEmail
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching group messages: {ex.Message}");
            }

            return messages;
        }

        [HttpPost]
        public IActionResult MarkMessagesAsRead(string otherUserId)
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false });
                }

                var otherUser = GetUserById(otherUserId);
                if (otherUser == null)
                {
                    return Json(new { success = false });
                }

                var result = MarkMessagesAsRead(userEmail, otherUser.Email);
                return Json(new { success = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error marking messages as read: {ex.Message}");
                return Json(new { success = false });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MarkGroupMessagesAsRead(int groupId)
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                var userId = HttpContext.Session.GetString("UserId");

                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false });
                }

                var result = MarkGroupMessagesAsReadForUser(userEmail, groupId);

                // Return updated chat list
                if (result)
                {
                    var users = GetAllUsersWithLastMessage(userId, userEmail);
                    var groups = GetUserGroups(userEmail);
                    var combinedChats = BuildCombinedChats(users, groups, userId, userEmail);

                    // Broadcast to update this user's chat list UI
                    await _chatHub.Clients.Group($"chatlist_{userId}").SendAsync("UpdateChatList");

                    return Json(new
                    {
                        success = true,
                        chats = combinedChats,
                        unreadMessages = GetUnreadMessagesCount(userEmail)
                    });
                }

                return Json(new { success = false });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error marking group messages as read: {ex.Message}");
                return Json(new { success = false });
            }
        }

        private bool MarkMessagesAsRead(string userEmail, string otherUserEmail)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    var query = @"
                UPDATE messages 
                SET is_read = 1 
                WHERE receiver_email = @UserEmail 
                AND sender_email = @OtherUserEmail 
                AND is_read = 0";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@UserEmail", userEmail);
                        command.Parameters.AddWithValue("@OtherUserEmail", otherUserEmail);

                        int rowsAffected = command.ExecuteNonQuery();
                        return rowsAffected >= 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in MarkMessagesAsRead: {ex.Message}");
                return false;
            }
        }

        private bool MarkGroupMessagesAsReadForUser(string userEmail, int groupId)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // First, ensure the group_message_read_status table exists
                    EnsureGroupMessageReadStatusTableExists(connection);

                    // Get all unread messages for this group that the user hasn't marked as read
                    var getUnreadMessagesQuery = @"
                SELECT gm.id
                FROM group_messages gm
                INNER JOIN group_members gmm ON gm.group_id = gmm.group_id
                WHERE gm.group_id = @GroupId
                AND gmm.student_email = @UserEmail
                AND gm.sender_email != @UserEmail
                AND NOT EXISTS (
                    SELECT 1 FROM group_message_read_status gmrs 
                    WHERE gmrs.group_message_id = gm.id 
                    AND gmrs.user_email = @UserEmail
                    AND gmrs.has_read = TRUE
                )";

                    var unreadMessageIds = new List<int>();
                    using (var command = new NpgsqlCommand(getUnreadMessagesQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                unreadMessageIds.Add(Convert.ToInt32(reader["id"]));
                            }
                        }
                    }

                    // Mark each unread message as read for this specific user
                    if (unreadMessageIds.Count > 0)
                    {
                        var insertQuery = @"
                    INSERT INTO group_message_read_status (group_message_id, user_email, has_read, read_at) 
                    VALUES (@MessageId, @UserEmail, TRUE, NOW())
                    ON CONFLICT (group_message_id, user_email) DO UPDATE SET has_read = TRUE, read_at = NOW()";

                        foreach (var messageId in unreadMessageIds)
                        {
                            using (var command = new NpgsqlCommand(insertQuery, connection))
                            {
                                command.Parameters.AddWithValue("@MessageId", messageId);
                                command.Parameters.AddWithValue("@UserEmail", userEmail);
                                command.ExecuteNonQuery();
                            }
                        }
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in MarkGroupMessagesAsReadForUser: {ex.Message}");
                return false;
            }
        }

        private void EnsureGroupMessagesTableExists(NpgsqlConnection connection)
        {
            try
            {
                // First check if column 'id' exists
                var checkColumnQuery = @"
                    SELECT count(*) FROM information_schema.columns 
                    WHERE table_name = 'group_messages' AND column_name = 'id'";

                bool hasId = false;
                using (var cmd = new NpgsqlCommand(checkColumnQuery, connection))
                {
                    hasId = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }

                if (!hasId)
                {
                    Console.WriteLine("[EnsureGroupMessagesTableExists] Adding 'id' column to group_messages");
                    var alterQuery = @"ALTER TABLE group_messages ADD COLUMN id SERIAL PRIMARY KEY";
                    using (var cmd = new NpgsqlCommand(alterQuery, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error ensuring group_messages schema: {ex.Message}");
            }
        }

        private void EnsureGroupMessageReadStatusTableExists(NpgsqlConnection connection)
        {
            try
            {
                var createTableQuery = @"
            CREATE TABLE IF NOT EXISTS group_message_read_status (
                id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                group_message_id INT NOT NULL,
                user_email VARCHAR(255) NOT NULL,
                has_read BOOLEAN DEFAULT FALSE,
                read_at TIMESTAMP,
                created_at TIMESTAMP DEFAULT NOW(),
                UNIQUE (group_message_id, user_email),
                FOREIGN KEY (group_message_id) REFERENCES group_messages(id) ON DELETE CASCADE
            )";

                using (var command = new NpgsqlCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error ensuring table exists: {ex.Message}");
            }
        }

        [HttpGet]
        public IActionResult GetGroupMembers(int groupId)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                var members = new List<object>();
                string groupCreator = "";

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Get group creator
                    var creatorQuery = "SELECT created_by FROM \"groups\" WHERE group_id = @GroupId";
                    using (var command = new NpgsqlCommand(creatorQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        var result = command.ExecuteScalar();
                        if (result != null)
                        {
                            groupCreator = result.ToString();
                        }
                    }

                    // Get all members
                    var query = @"SELECT s.id, s.firstname, s.lastname, s.email, s.photo, gm.joined_at
                                 FROM group_members gm
                                 INNER JOIN students s ON gm.student_email = s.email
                                 WHERE gm.group_id = @GroupId
                                 ORDER BY gm.joined_at ASC";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string memberEmail = reader["email"].ToString();
                                byte[] photoData = null;
                                if (reader["photo"] != DBNull.Value)
                                {
                                    photoData = (byte[])reader["photo"];
                                }

                                string photoBase64 = photoData != null ? Convert.ToBase64String(photoData) : null;

                                members.Add(new
                                {
                                    userId = reader["id"].ToString(),
                                    firstName = reader["firstname"].ToString(),
                                    lastName = reader["lastname"].ToString(),
                                    fullName = $"{reader["firstname"]} {reader["lastname"]}",
                                    email = memberEmail,
                                    photoBase64 = photoBase64,
                                    canRemove = userEmail == groupCreator && memberEmail != groupCreator
                                });
                            }
                        }
                    }
                }

                return Json(new { success = true, members = members });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting group members: {ex.Message}");
                return Json(new { success = false, message = "Error loading group members" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RemoveMemberFromGroup(int groupId, string memberEmail)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Check if user is group creator
                    var checkQuery = "SELECT created_by FROM \"groups\" WHERE group_id = @GroupId";
                    using (var command = new NpgsqlCommand(checkQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        var createdBy = command.ExecuteScalar()?.ToString();

                        if (createdBy != userEmail)
                        {
                            return Json(new { success = false, message = "Only group creator can remove members" });
                        }
                    }

                    // Don't allow removing the creator
                    var deleteQuery = "DELETE FROM group_members WHERE group_id = @GroupId AND student_email = @MemberEmail AND student_email != (SELECT created_by FROM \"groups\" WHERE group_id = @GroupId)";
                    using (var command = new NpgsqlCommand(deleteQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@MemberEmail", memberEmail);

                        int rowsAffected = command.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            // Broadcast to all group members via SignalR
                            await _chatHub.Clients.Group($"group_{groupId}").SendAsync("GroupMemberRemoved", new { groupId, memberEmail });

                            return Json(new { success = true, message = "Member removed successfully" });
                        }
                        else
                        {
                            return Json(new { success = false, message = "Failed to remove member or cannot remove group creator" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing member: {ex.Message}");
                return Json(new { success = false, message = "Error removing member from group" });
            }
        }

        [HttpGet]
        public IActionResult GetAvailableMembers(int groupId)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                var availableMembers = new List<object>();

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    var query = @"SELECT s.id, s.firstname, s.lastname, s.email, s.photo
                                 FROM students s
                                 WHERE s.status = 'Active'
                                 AND s.email NOT IN (
                                     SELECT student_email FROM group_members WHERE group_id = @GroupId
                                 )
                                 AND s.email != @UserEmail
                                 ORDER BY s.firstname, s.lastname";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                byte[] photoData = null;
                                if (reader["photo"] != DBNull.Value)
                                {
                                    photoData = (byte[])reader["photo"];
                                }

                                string photoBase64 = photoData != null ? Convert.ToBase64String(photoData) : null;

                                availableMembers.Add(new
                                {
                                    userId = reader["id"].ToString(),
                                    firstName = reader["firstname"].ToString(),
                                    lastName = reader["lastname"].ToString(),
                                    fullName = $"{reader["firstname"]} {reader["lastname"]}",
                                    email = reader["email"].ToString(),
                                    photoBase64 = photoBase64
                                });
                            }
                        }
                    }
                }

                return Json(new { success = true, availableMembers = availableMembers });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting available members: {ex.Message}");
                return Json(new { success = false, message = "Error loading available members" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AddMembersToGroup([FromBody] AddMembersModel model)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            if (model.MemberEmails == null || !model.MemberEmails.Any())
            {
                return Json(new { success = false, message = "No members selected" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Check if user is group creator
                    var checkQuery = "SELECT created_by FROM \"groups\" WHERE group_id = @GroupId";
                    using (var command = new NpgsqlCommand(checkQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", model.GroupId);
                        var createdBy = command.ExecuteScalar()?.ToString();

                        if (createdBy != userEmail)
                        {
                            return Json(new { success = false, message = "Only group creator can add members" });
                        }
                    }

                    var insertQuery = "INSERT INTO group_members (group_id, student_email, joined_at) VALUES (@GroupId, @StudentEmail, NOW())";

                    foreach (var memberEmail in model.MemberEmails)
                    {
                        using (var command = new NpgsqlCommand(insertQuery, connection))
                        {
                            command.Parameters.AddWithValue("@GroupId", model.GroupId);
                            command.Parameters.AddWithValue("@StudentEmail", memberEmail);
                            command.ExecuteNonQuery();
                        }
                    }

                    // Broadcast to all group members via SignalR
                    await _chatHub.Clients.Group($"group_{model.GroupId}").SendAsync("GroupMembersAdded", new { groupId = model.GroupId, memberEmails = model.MemberEmails });

                    return Json(new { success = true, message = "Members added successfully" });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding members: {ex.Message}");
                return Json(new { success = false, message = "Error adding members to group" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateGroup([FromForm] UpdateGroupModel model)
        {
            var userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not logged in" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Check if user is group creator
                    var checkQuery = "SELECT created_by FROM \"groups\" WHERE group_id = @GroupId";
                    using (var command = new NpgsqlCommand(checkQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", model.GroupId);
                        var createdBy = command.ExecuteScalar()?.ToString();

                        if (createdBy != userEmail)
                        {
                            return Json(new { success = false, message = "Only group creator can edit group" });
                        }
                    }

                    string query;
                    NpgsqlCommand updateCommand;
                    string groupImageBase64 = null;

                    if (model.GroupImage != null && model.GroupImage.Length > 0)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            model.GroupImage.CopyTo(memoryStream);
                            var imageData = memoryStream.ToArray();
                            groupImageBase64 = Convert.ToBase64String(imageData);

                            query = "UPDATE \"groups\" SET group_name = @GroupName, group_image = @GroupImage, updated_at = NOW() WHERE group_id = @GroupId";
                            updateCommand = new NpgsqlCommand(query, connection);
                            updateCommand.Parameters.AddWithValue("@GroupName", model.GroupName);
                            updateCommand.Parameters.AddWithValue("@GroupImage", imageData);
                            updateCommand.Parameters.AddWithValue("@GroupId", model.GroupId);
                        }
                    }
                    else
                    {
                        query = "UPDATE \"groups\" SET group_name = @GroupName, updated_at = NOW() WHERE group_id = @GroupId";
                        updateCommand = new NpgsqlCommand(query, connection);
                        updateCommand.Parameters.AddWithValue("@GroupName", model.GroupName);
                        updateCommand.Parameters.AddWithValue("@GroupId", model.GroupId);
                    }

                    int rowsAffected = updateCommand.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        if (groupImageBase64 == null)
                        {
                            // Get existing image if no new image was uploaded
                            var getImageQuery = "SELECT group_image FROM \"groups\" WHERE group_id = @GroupId";
                            using (var getImageCommand = new NpgsqlCommand(getImageQuery, connection))
                            {
                                getImageCommand.Parameters.AddWithValue("@GroupId", model.GroupId);
                                var result = getImageCommand.ExecuteScalar();
                                if (result != null && result != DBNull.Value)
                                {
                                    var existingImage = (byte[])result;
                                    groupImageBase64 = Convert.ToBase64String(existingImage);
                                }
                            }
                        }

                        // REAL-TIME BROADCAST
                        await BroadcastGroupUpdate(model.GroupId, model.GroupName, groupImageBase64);

                        return Json(new
                        {
                            success = true,
                            message = "Group updated successfully",
                            groupImageBase64 = groupImageBase64
                        });
                    }
                    else
                    {
                        return Json(new { success = false, message = "Failed to update group" });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating group: {ex.Message}");
                return Json(new { success = false, message = "Error updating group" });
            }
        }

        private List<UserInfo> GetAllUsersWithLastMessage(string currentUserId, string currentUserEmail)
        {
            var users = new List<UserInfo>();

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    string query = @"
            SELECT 
                s.id, 
                s.firstname, 
                s.lastname, 
                s.email, 
                s.status, 
                s.photo,
                COALESCE(MAX(m.sent_at), '0001-01-01') as last_message_time,
                COUNT(CASE WHEN LOWER(m.receiver_email) = LOWER(@CurrentUserEmail) AND m.is_read = 0 AND LOWER(m.sender_email) = LOWER(s.email) THEN 1 END) as unread_count,
                COALESCE(
                    (SELECT m2.message 
                     FROM messages m2 
                     WHERE (LOWER(m2.sender_email) = LOWER(s.email) AND LOWER(m2.receiver_email) = LOWER(@CurrentUserEmail))
                        OR (LOWER(m2.sender_email) = LOWER(@CurrentUserEmail) AND LOWER(m2.receiver_email) = LOWER(s.email))
                     ORDER BY m2.sent_at DESC 
                     LIMIT 1), '') as last_message_preview
            FROM students s
            LEFT JOIN messages m ON (
                (LOWER(m.sender_email) = LOWER(s.email) AND LOWER(m.receiver_email) = LOWER(@CurrentUserEmail)) 
                OR 
                (LOWER(m.sender_email) = LOWER(@CurrentUserEmail) AND LOWER(m.receiver_email) = LOWER(s.email))
            )
            WHERE s.status = 'Active' AND s.id::text != @CurrentUserId
            GROUP BY s.id, s.firstname, s.lastname, s.email, s.status, s.photo
            ORDER BY last_message_time DESC, s.firstname ASC, s.lastname ASC";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@CurrentUserId", currentUserId);
                        command.Parameters.AddWithValue("@CurrentUserEmail", currentUserEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string userId = reader["id"].ToString();
                                string firstName = reader["firstname"].ToString();
                                string lastName = reader["lastname"].ToString();
                                string email = reader["email"].ToString();
                                DateTime lastMessageTime = reader["last_message_time"] != DBNull.Value ?
                                    Convert.ToDateTime(reader["last_message_time"]) : DateTime.MinValue;
                                int unreadCount = Convert.ToInt32(reader["unread_count"]);
                                string lastMessagePreview = reader["last_message_preview"].ToString();

                                byte[] photoData = null;
                                if (reader["photo"] != DBNull.Value)
                                {
                                    photoData = (byte[])reader["photo"];
                                }

                                users.Add(new UserInfo
                                {
                                    UserId = userId,
                                    FirstName = firstName,
                                    LastName = lastName,
                                    FullName = $"{firstName} {lastName}",
                                    Email = email,
                                    PhotoData = photoData,
                                    LastMessageTime = lastMessageTime,
                                    UnreadCount = unreadCount,
                                    LastMessagePreview = lastMessagePreview
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching users with last message: {ex.Message}");
                return GetAllUsers(currentUserId);
            }

            return users;
        }

        private List<CombinedChatEntry> GetUnifiedChatsInternal(string currentUserId, string currentUserEmail, string selectedUserId = null, string selectedGroupId = null)
        {
            try
            {
                Console.WriteLine($"[GetUnifiedChatsInternal] Fetching for user: {currentUserEmail}");
                var users = GetAllUsersWithLastMessage(currentUserId, currentUserEmail);
                Console.WriteLine($"[GetUnifiedChatsInternal] Found {users?.Count ?? 0} users");

                var groups = GetUserGroups(currentUserEmail);
                Console.WriteLine($"[GetUnifiedChatsInternal] Found {groups?.Count ?? 0} groups");

                var combined = BuildCombinedChats(users, groups, selectedUserId, selectedGroupId);
                Console.WriteLine($"[GetUnifiedChatsInternal] Built {combined?.Count ?? 0} combined chats");

                return combined;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetUnifiedChatsInternal] CRITICAL ERROR: {ex.Message}");
                Console.WriteLine($"[GetUnifiedChatsInternal] Stack: {ex.StackTrace}");
                return new List<CombinedChatEntry>();
            }
        }

        [HttpGet]
        public IActionResult GetUnifiedChats()
        {
            var userId = HttpContext.Session.GetString("UserId");
            var userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userEmail))
            {
                return Json(new { success = false, message = "User not authenticated" });
            }

            var chats = GetUnifiedChatsInternal(userId, userEmail, null, null);
            return Json(new { success = true, chats });
        }

        // Video Call History Methods
        [HttpPost]
        public async Task<IActionResult> StartVideoCall([FromBody] StartCallRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                {
                    return Json(new { success = false, message = "User not authenticated" });
                }

                // Generate a unique call ID
                var callId = Guid.NewGuid().ToString();

                // Save call to database
                await _videoCallHistoryService.StartCallAsync(
                    callId,
                    int.Parse(userId),
                    request.ReceiverType,
                    request.ReceiverId,
                    "Video");

                return Json(new
                {
                    success = true,
                    callId = callId,
                    message = "Call started successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting video call");
                return Json(new
                {
                    success = false,
                    message = $"Error starting call: {ex.Message}"
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCallStatus([FromBody] UpdateCallStatusRequest request)
        {
            try
            {
                var success = await _videoCallHistoryService.UpdateCallStatusAsync(
                    request.CallId, request.Status);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating call status");
                return Json(new { success = false, message = "Error updating call status" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> EndVideoCall([FromBody] EndCallRequest request)
        {
            try
            {
                var success = await _videoCallHistoryService.UpdateCallStatusAsync(
                    request.CallId, request.Status);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending video call");
                return Json(new { success = false, message = "Error ending call" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveCallDurationMessage([FromBody] SaveCallDurationRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                var userEmail = HttpContext.Session.GetString("Email");
                var userName = HttpContext.Session.GetString("FirstName") + " " + HttpContext.Session.GetString("LastName");

                if (string.IsNullOrEmpty(userId))
                {
                    return Json(new { success = false, message = "User not authenticated" });
                }

                // Determine if this is a group or individual call
                if (request.CallType == "Group" && !string.IsNullOrEmpty(request.GroupId))
                {
                    // Save group call message
                    await SaveGroupCallDurationMessage(request, userEmail, userName);
                }
                else
                {
                    // Save individual call message
                    await SaveIndividualCallDurationMessage(request, userEmail);
                }

                // Update the call status and duration in video call history
                await _videoCallHistoryService.UpdateCallStatusAsync(request.CallId, "Completed");
                await _videoCallHistoryService.UpdateCallDurationAsync(request.CallId, request.Duration);

                return Json(new { success = true, message = "Call duration saved successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving call duration message");
                return Json(new { success = false, message = "Error saving call duration" });
            }
        }

        private async Task SaveIndividualCallDurationMessage(SaveCallDurationRequest request, string userEmail)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();

                // Get receiver email
                var receiverEmail = await GetUserEmailById(request.ReceiverId);
                if (string.IsNullOrEmpty(receiverEmail))
                {
                    throw new Exception("Receiver not found");
                }

                // Create the call duration message
                var callMessage = $"Video call ended (Duration: {request.FormattedDuration})";

                var query = @"INSERT INTO messages (sender_email, receiver_email, message, sent_at, is_read, is_call_message, call_duration, call_status) 
                     VALUES (@SenderEmail, @ReceiverEmail, @Message, NOW(), 0, 1, @CallDuration, @CallStatus)";

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SenderEmail", userEmail);
                    command.Parameters.AddWithValue("@ReceiverEmail", receiverEmail);
                    command.Parameters.AddWithValue("@Message", callMessage);
                    command.Parameters.AddWithValue("@CallDuration", request.FormattedDuration);
                    command.Parameters.AddWithValue("@CallStatus", "Completed");
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task<string> GetUserEmailById(string userId)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();

                var query = "SELECT \"email\" FROM students WHERE \"id\" = @UserId";
                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UserId", int.Parse(userId));
                    var result = await command.ExecuteScalarAsync();
                    return result?.ToString();
                }
            }
        }


        private async Task SaveGroupCallDurationMessage(SaveCallDurationRequest request, string userEmail, string userName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();

                // Create the call duration message
                var callMessage = $"{userName} ended a video call (Duration: {request.FormattedDuration})";

                var query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read, is_call_message, call_duration, call_status) 
                     VALUES (@GroupId, @SenderEmail, @Message, NOW(), 0, 1, @CallDuration, @CallStatus)";

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@GroupId", request.GroupId);
                    command.Parameters.AddWithValue("@SenderEmail", userEmail);
                    command.Parameters.AddWithValue("@Message", callMessage);
                    command.Parameters.AddWithValue("@CallDuration", request.FormattedDuration);
                    command.Parameters.AddWithValue("@CallStatus", "Completed");
                    await command.ExecuteNonQueryAsync();
                }

                // Update group last activity
                var updateQuery = "UPDATE `groups` SET last_activity = NOW() WHERE group_id = @GroupId";
                using (var command = new NpgsqlCommand(updateQuery, connection))
                {
                    command.Parameters.AddWithValue("@GroupId", request.GroupId);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        [HttpGet]
        public IActionResult DebugUserStatus()
        {
            var userId = HttpContext.Session.GetString("UserId");
            var userEmail = HttpContext.Session.GetString("Email");
            var userName = HttpContext.Session.GetString("FirstName") + " " + HttpContext.Session.GetString("LastName");

            var isAuthenticated = HttpContext.User?.Identity?.IsAuthenticated ?? false;
            var authUserId = HttpContext.User?.FindFirst("UserId")?.Value;

            return Json(new
            {
                success = true,
                sessionUserId = userId,
                sessionEmail = userEmail,
                sessionUserName = userName,
                isAuthenticated = isAuthenticated,
                authUserId = authUserId,
                connectionId = HttpContext.Connection?.Id
            });
        }

        [HttpGet]
        public IActionResult DebugSignalRConnections()
        {
            // This would require storing connection info, but for now just return basic info
            return Json(new
            {
                success = true,
                message = "Debug endpoint - implement connection tracking if needed"
            });
        }


        [HttpGet]
        public async Task<IActionResult> GetCallHistory()
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                if (string.IsNullOrEmpty(userId))
                    return Json(new { success = false, message = "User not authenticated" });

                var callHistory = await _videoCallHistoryService.GetCallHistoryAsync(int.Parse(userId));
                return Json(new { success = true, callHistory = callHistory });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting call history");
                return Json(new { success = false, message = "Error getting call history" });
            }
        }

        [HttpPost]
        public IActionResult DebugStartVideoCall([FromBody] DebugCallRequest request)
        {
            try
            {
                var userId = HttpContext.Session.GetString("UserId");
                var userEmail = HttpContext.Session.GetString("Email");

                Console.WriteLine("=== DEBUG START VIDEO CALL ===");
                Console.WriteLine($"User ID: {userId}");
                Console.WriteLine($"User Email: {userEmail}");
                Console.WriteLine($"Receiver ID: {request.ReceiverId}");
                Console.WriteLine($"Receiver Type: {request.ReceiverType}");

                // Simulate call creation to see what's happening
                var callId = Guid.NewGuid().ToString();
                Console.WriteLine($"Generated Call ID: {callId}");

                return Json(new
                {
                    success = true,
                    callId = callId,
                    debug = true,
                    message = "Debug call created successfully"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Debug Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        public class DebugCallRequest
        {
            public string ReceiverId { get; set; }
            public string ReceiverType { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateCallParticipants([FromBody] UpdateParticipantsRequest request)
        {
            try
            {
                var success = await _videoCallHistoryService.UpdateParticipantsCountAsync(
                    request.CallId, request.ParticipantsCount);

                return Json(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating call participants");
                return Json(new { success = false, message = "Error updating participants" });
            }
        }

        // ========================================
        // UNREAD MESSAGE MANAGEMENT ENDPOINTS FOR REALTIME-CHAT.JS
        // ========================================

        [HttpPost]
        public IActionResult MarkMessagesAsReadByUserId(string otherUserId)
        {
            var currentUserEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(currentUserEmail) || string.IsNullOrEmpty(otherUserId))
            {
                return Json(new { success = false, message = "Invalid request" });
            }

            try
            {
                // Get the other user's email from their ID
                string otherUserEmail = null;
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var userQuery = "SELECT email FROM students WHERE id = @UserId";
                    using (var command = new NpgsqlCommand(userQuery, connection))
                    {
                        command.Parameters.AddWithValue("@UserId", int.Parse(otherUserId));
                        var result = command.ExecuteScalar();
                        if (result != null)
                        {
                            otherUserEmail = result.ToString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(otherUserEmail))
                {
                    return Json(new { success = false, message = "User not found" });
                }

                // Mark messages as read
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = @"UPDATE messages 
                                 SET is_read = true 
                                 WHERE receiver_email = @CurrentUserEmail 
                                 AND sender_email = @OtherUserEmail 
                                 AND is_read = false";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@CurrentUserEmail", currentUserEmail);
                        command.Parameters.AddWithValue("@OtherUserEmail", otherUserEmail);
                        var rowsAffected = command.ExecuteNonQuery();
                        Console.WriteLine($"Marked {rowsAffected} messages as read for user {otherUserId}");
                    }
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error marking messages as read: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }


        [HttpPost]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            TempData["SuccessMessage"] = "You have been logged out successfully.";
            return RedirectToAction("UserLogin", "Account");
        }
    }



    // Video Call Request Models
    public class StartCallRequest
    {
        public string ReceiverId { get; set; }
        public string ReceiverType { get; set; } // "Student" or "Group"
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
    // Existing Model Classes
    public class AddMembersModel
    {
        public int GroupId { get; set; }
        public List<string> MemberEmails { get; set; }
    }

    public class MessageViewModel
    {
        // ... existing properties ...

        public bool IsCallMessage { get; set; }
        public string CallDuration { get; set; }
        public string CallStatus { get; set; }

        // Add this for group call participants
        public List<CallParticipantInfo> Participants { get; set; }
    }

    public class CallParticipantInfo
    {
        public string UserName { get; set; }
        public int Duration { get; set; }
        public string FormattedDuration { get; set; }
    }

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
}