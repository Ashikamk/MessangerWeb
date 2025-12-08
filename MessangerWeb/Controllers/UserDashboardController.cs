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
using WebsiteApplication.Services;
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
        private readonly IHubContext<ChatHub> _hubContext;

        public UserDashboardController(
            IVideoCallHistoryService videoCallHistoryService,
            IVideoCallParticipantService videoCallParticipantService,
            ILogger<UserDashboardController> logger,
            IConfiguration configuration,
            IHubContext<ChatHub> hubContext)
        {
            _videoCallHistoryService = videoCallHistoryService;
            _videoCallParticipantService = videoCallParticipantService;
            _logger = logger;
            connectionString = configuration.GetConnectionString("DefaultConnection");
            _hubContext = hubContext;
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

            var model = new UserDashboardViewModel
            {
                FullName = $"{firstName} {lastName}".Trim(),
                UserId = userId,
                UserEmail = userEmail,
                UserPhotoBase64 = currentUser?.PhotoBase64,
                Users = GetAllUsersWithLastMessage(userId, userEmail),
                Groups = GetUserGroups(userEmail)
            };

            if (!string.IsNullOrEmpty(selectedUserId))
            {
                model.SelectedUser = GetUserById(selectedUserId);
                if (model.SelectedUser != null)
                {
                    model.Messages = GetMessages(userEmail, model.SelectedUser.Email);
                    model.CurrentViewType = "user";
                    // Mark messages as read immediately when chat is opened
                    MarkMessagesAsRead(userEmail, model.SelectedUser.Email);
                }
            }
            else if (selectedGroupId.HasValue)
            {
                model.SelectedGroup = model.Groups.FirstOrDefault(g => g.GroupId == selectedGroupId.Value);
                if (model.SelectedGroup != null)
                {
                    model.GroupMessages = GetGroupMessagesByGroupId(selectedGroupId.Value, userEmail);
                    model.CurrentViewType = "group";
                    // Mark group messages as read immediately when chat is opened - FOR CURRENT USER ONLY
                    MarkGroupMessagesAsReadForUser(userEmail, selectedGroupId.Value);
                }
            }

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(string receiverId, string messageText)
        {
            var senderId = HttpContext.Session.GetString("UserId");
            var senderEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(receiverId) || string.IsNullOrEmpty(messageText))
            {
                return Json(new { success = false, message = "Invalid message data" });
            }

            try
            {
                var receiverUser = GetUserById(receiverId);
                if (receiverUser == null)
                {
                    return Json(new { success = false, message = "Receiver not found" });
                }

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    var query = @"INSERT INTO messages (sender_email, receiver_email, message, sent_at, is_read) 
                                 VALUES (@SenderEmail, @ReceiverEmail, @Message, NOW(), 0)";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@ReceiverEmail", receiverUser.Email);
                        command.Parameters.AddWithValue("@Message", messageText);
                        command.ExecuteNonQuery();
                    }
                }

                // Broadcast via SignalR
                var messageData = new 
                { 
                    senderId = senderId, 
                    message = messageText, 
                    sentAt = DateTime.UtcNow 
                };
                
                await _hubContext.Clients.Group($"user_{receiverId}").SendAsync("ReceiveMessage", messageData);
                await _hubContext.Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", messageData);

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

            if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(receiverId) || file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Invalid file data" });
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
                        command.Parameters.AddWithValue("@FilePath", isImage ? null : relativePath);
                        command.Parameters.AddWithValue("@ImagePath", isImage ? relativePath : null);
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
                    fileOriginalName = file.FileName
                };
                
                await _hubContext.Clients.Group($"user_{receiverId}").SendAsync("ReceiveMessage", messageData);
                await _hubContext.Clients.Group($"user_{senderId}").SendAsync("ReceiveMessage", messageData);

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
        public IActionResult UpdateProfile(ProfileUpdateModel model)
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

                    if (model.ProfileImage != null && model.ProfileImage.Length > 0)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            model.ProfileImage.CopyTo(memoryStream);
                            var photoData = memoryStream.ToArray();

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

                        return Json(new { success = true, message = "Profile updated successfully" });
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

            if (string.IsNullOrEmpty(senderEmail) || groupId <= 0 || string.IsNullOrEmpty(messageText))
            {
                return Json(new { success = false, message = "Invalid message data" });
            }

            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    // Insert message with is_read = 0 (unread for everyone initially)
                    var query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read) 
                         VALUES (@GroupId, @SenderEmail, @Message, NOW(), 0);
                         UPDATE `groups` SET last_activity = NOW() WHERE group_id = @GroupId;";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@Message", messageText);
                        command.ExecuteNonQuery();
                    }
                }

                // Broadcast via SignalR
                var messageData = new 
                { 
                    senderId = senderId, 
                    message = messageText, 
                    sentAt = DateTime.UtcNow,
                    senderName = userName, // Include sender name for group chat
                    groupId = groupId
                };
                
                await _hubContext.Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", messageData);

                return Json(new { success = true });
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

            if (string.IsNullOrEmpty(senderEmail) || groupId <= 0 || file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "Invalid file data" });
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
                         UPDATE `groups` SET last_activity = NOW() WHERE group_id = @GroupId;";
                    }
                    else
                    {
                        query = @"INSERT INTO group_messages (group_id, sender_email, message, sent_at, is_read, file_path, file_original_name) 
                         VALUES (@GroupId, @SenderEmail, '', NOW(), 0, @FilePath, @FileOriginalName);
                         UPDATE `groups` SET last_activity = NOW() WHERE group_id = @GroupId;";
                    }

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@SenderEmail", senderEmail);
                        command.Parameters.AddWithValue("@FilePath", isImage ? null : relativePath);
                        command.Parameters.AddWithValue("@ImagePath", isImage ? relativePath : null);
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
                
                await _hubContext.Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupMessage", messageData);

                return Json(new { success = true, fileName = file.FileName, isImage = isImage });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult GetUnreadMessagesCount()
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false, unreadMessages = new Dictionary<string, int>() });
                }

                var unreadCounts = GetUnreadMessagesCount(userEmail);
                return Json(new { success = true, unreadMessages = unreadCounts });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting unread messages count: {ex.Message}");
                return Json(new { success = false, unreadMessages = new Dictionary<string, int>() });
            }
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

                    // Get unread individual messages
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
                                unreadCounts[userId] = count;
                            }
                        }
                    }

                    // Get unread group messages - FIXED QUERY
                    var groupQuery = @"
                SELECT g.group_id, COUNT(*) as unread_count
                FROM group_messages gm
                INNER JOIN `groups` g ON gm.group_id = g.group_id
                INNER JOIN group_members gm2 ON g.group_id = gm2.group_id
                WHERE gm2.student_email = @UserEmail
                AND gm.sender_email != @UserEmail
                AND NOT EXISTS (
                    SELECT 1 FROM group_message_read_status gmrs 
                    WHERE gmrs.group_message_id = gm.message_id 
                    AND gmrs.user_email = @UserEmail
                    AND gmrs.has_read = 1
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
                                unreadCounts[groupId] = count;
                                Console.WriteLine($"Found {count} unread messages for group {groupId}");
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
                    var query = @"SELECT g.*, 
                         (SELECT COUNT(*) FROM group_members gm WHERE gm.group_id = g.group_id) as member_count
                         FROM ""groups"" g
                         INNER JOIN group_members gm ON g.group_id = gm.group_id
                         WHERE gm.student_email = @UserEmail
                         ORDER BY g.last_activity DESC, g.group_name ASC";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@UserEmail", userEmail);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                byte[] groupImage = null;
                                if (reader["group_image"] != DBNull.Value)
                                {
                                    groupImage = (byte[])reader["group_image"];
                                }

                                groups.Add(new GroupInfo
                                {
                                    GroupId = Convert.ToInt32(reader["group_id"]),
                                    GroupName = reader["group_name"].ToString(),
                                    CreatedBy = reader["created_by"].ToString(),
                                    CreatedAt = Convert.ToDateTime(reader["created_at"]),
                                    GroupImage = groupImage,
                                    UpdatedAt = Convert.ToDateTime(reader["updated_at"]),
                                    LastActivity = Convert.ToDateTime(reader["last_activity"]),
                                    MemberCount = Convert.ToInt32(reader["member_count"])
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching groups: {ex.Message}");
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
                             WHERE gmrs.group_message_id = gm.message_id 
                             AND gmrs.user_email = @CurrentUserEmail
                             AND gmrs.has_read = 1
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

                                messages.Add(new GroupMessage
                                {
                                    MessageId = Convert.ToInt32(reader["message_id"]),
                                    GroupId = Convert.ToInt32(reader["group_id"]),
                                    SenderEmail = reader["sender_email"].ToString(),
                                    SenderName = reader["sender_name"].ToString(),
                                    MessageText = reader["message"]?.ToString() ?? "",
                                    MessageRtf = reader["message_rtf"]?.ToString(),
                                    ImagePath = reader["image_path"]?.ToString(),
                                    FilePath = reader["file_path"]?.ToString(),
                                    FileOriginalName = reader["file_original_name"]?.ToString(),
                                    SentAt = DateTime.SpecifyKind(Convert.ToDateTime(reader["sent_at"]), DateTimeKind.Utc),
                                    IsRead = isReadByCurrentUser,
                                    IsCurrentUserSender = reader["sender_email"].ToString() == currentUserEmail,
                                    // Add call message fields
                                    IsCallMessage = reader["is_call_message"] != DBNull.Value && Convert.ToBoolean(reader["is_call_message"]),
                                    CallDuration = reader["call_duration"]?.ToString(),
                                    CallStatus = reader["call_status"]?.ToString()
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
        public IActionResult MarkGroupMessagesAsRead(int groupId)
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("Email");
                if (string.IsNullOrEmpty(userEmail))
                {
                    return Json(new { success = false });
                }

                var result = MarkGroupMessagesAsReadForUser(userEmail, groupId);

                // Return updated unread counts
                if (result)
                {
                    var unreadCounts = GetUnreadMessagesCount(userEmail);
                    return Json(new { success = true, unreadMessages = unreadCounts });
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
                SELECT gm.message_id
                FROM group_messages gm
                INNER JOIN group_members gmm ON gm.group_id = gmm.group_id
                WHERE gm.group_id = @GroupId
                AND gmm.student_email = @UserEmail
                AND gm.sender_email != @UserEmail
                AND NOT EXISTS (
                    SELECT 1 FROM group_message_read_status gmrs 
                    WHERE gmrs.group_message_id = gm.message_id 
                    AND gmrs.user_email = @UserEmail
                    AND gmrs.has_read = 1
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
                                unreadMessageIds.Add(Convert.ToInt32(reader["message_id"]));
                            }
                        }
                    }

                    // Mark each unread message as read for this specific user
                    if (unreadMessageIds.Count > 0)
                    {
                        var insertQuery = @"
                    INSERT INTO group_message_read_status (group_message_id, user_email, has_read, read_at) 
                    VALUES (@MessageId, @UserEmail, 1, NOW())
                    ON DUPLICATE KEY UPDATE has_read = 1, read_at = NOW()";

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

        private void EnsureGroupMessageReadStatusTableExists(NpgsqlConnection connection)
        {
            try
            {
                var createTableQuery = @"
            CREATE TABLE IF NOT EXISTS group_message_read_status (
                id INT AUTO_INCREMENT PRIMARY KEY,
                group_message_id INT NOT NULL,
                user_email VARCHAR(255) NOT NULL,
                has_read BOOLEAN DEFAULT 0,
                read_at DATETIME,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY unique_message_user (group_message_id, user_email),
                FOREIGN KEY (group_message_id) REFERENCES group_messages(message_id) ON DELETE CASCADE
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
                    var creatorQuery = "SELECT created_by FROM `groups` WHERE group_id = @GroupId";
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
        public IActionResult RemoveMemberFromGroup(int groupId, string memberEmail)
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
                    var checkQuery = "SELECT created_by FROM `groups` WHERE group_id = @GroupId";
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
                    var deleteQuery = "DELETE FROM group_members WHERE group_id = @GroupId AND student_email = @MemberEmail AND student_email != (SELECT created_by FROM `groups` WHERE group_id = @GroupId)";
                    using (var command = new NpgsqlCommand(deleteQuery, connection))
                    {
                        command.Parameters.AddWithValue("@GroupId", groupId);
                        command.Parameters.AddWithValue("@MemberEmail", memberEmail);

                        int rowsAffected = command.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
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
        public IActionResult AddMembersToGroup([FromBody] AddMembersModel model)
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
                    var checkQuery = "SELECT created_by FROM `groups` WHERE group_id = @GroupId";
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
        public IActionResult UpdateGroup([FromForm] UpdateGroupModel model)
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
                    var checkQuery = "SELECT created_by FROM `groups` WHERE group_id = @GroupId";
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

                    if (model.GroupImage != null && model.GroupImage.Length > 0)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            model.GroupImage.CopyTo(memoryStream);
                            var imageData = memoryStream.ToArray();

                            query = "UPDATE `groups` SET group_name = @GroupName, group_image = @GroupImage, updated_at = NOW() WHERE group_id = @GroupId";
                            updateCommand = new NpgsqlCommand(query, connection);
                            updateCommand.Parameters.AddWithValue("@GroupName", model.GroupName);
                            updateCommand.Parameters.AddWithValue("@GroupImage", imageData);
                            updateCommand.Parameters.AddWithValue("@GroupId", model.GroupId);
                        }
                    }
                    else
                    {
                        query = "UPDATE `groups` SET group_name = @GroupName, updated_at = NOW() WHERE group_id = @GroupId";
                        updateCommand = new NpgsqlCommand(query, connection);
                        updateCommand.Parameters.AddWithValue("@GroupName", model.GroupName);
                        updateCommand.Parameters.AddWithValue("@GroupId", model.GroupId);
                    }

                    int rowsAffected = updateCommand.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        string groupImageBase64 = null;
                        if (model.GroupImage != null && model.GroupImage.Length > 0)
                        {
                            using (var memoryStream = new MemoryStream())
                            {
                                model.GroupImage.CopyTo(memoryStream);
                                groupImageBase64 = Convert.ToBase64String(memoryStream.ToArray());
                            }
                        }
                        else
                        {
                            // Get existing image if no new image was uploaded
                            var getImageQuery = "SELECT group_image FROM `groups` WHERE group_id = @GroupId";
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
                    COALESCE(MAX(m.sent_at), '0001-01-01') as last_message_time
                FROM students s
                LEFT JOIN messages m ON (
                    (m.sender_email = s.email AND m.receiver_email = @CurrentUserEmail) 
                    OR 
                    (m.sender_email = @CurrentUserEmail AND m.receiver_email = s.email)
                )
                WHERE s.status = 'Active' AND s.id != @CurrentUserId
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
                                    LastMessageTime = lastMessageTime
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




































