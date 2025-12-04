using System.Data;
using System.Text.Json;
using MessangerWeb.Services;
using Microsoft.Extensions.Logging;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace MessangerWeb.Services
{
    public interface IVideoCallParticipantService
    {
        Task<bool> AddParticipantAsync(string callId, int userId, string status);
        Task<bool> UpdateParticipantStatusAsync(string callId, int userId, string status, DateTime? joinedAt = null);
        Task<bool> UpdateParticipantDurationAsync(string callId, int userId, int duration);
        Task<List<VideoCallParticipant>> GetCallParticipantsAsync(string callId);
        Task<VideoCallDetails> GetCallDetailsAsync(string callId);
        Task<List<VideoCallDetails>> GetUserCallHistoryAsync(int userId);
    }

    public class VideoCallParticipantService : IVideoCallParticipantService
    {
        private readonly ILogger<VideoCallParticipantService> _logger;
        private readonly string _connectionString;

        public VideoCallParticipantService(IConfiguration configuration, ILogger<VideoCallParticipantService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _logger = logger;
        }

        public async Task<bool> AddParticipantAsync(string callId, int userId, string status)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
                var query = @"
                    INSERT INTO VideoCallParticipants 
                    (ParticipantId, CallId, UserId, Status, CreatedAt)
                    VALUES 
                    (@ParticipantId, @CallId, @UserId, @Status, NOW())";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@ParticipantId", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Status", status);

                var result = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Added participant {userId} to call {callId} with status {status}");
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding participant {userId} to call {callId}");
                return false;
            }
        }

        public async Task<bool> UpdateParticipantStatusAsync(string callId, int userId, string status, DateTime? joinedAt = null)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();

                string query;
                if (status == "Joined" && joinedAt.HasValue)
                {
                    query = @"
                        UPDATE VideoCallParticipants 
                        SET Status = @Status, JoinedAt = @JoinedAt 
                        WHERE CallId = @CallId AND UserId = @UserId";
                }
                else if (status == "Left")
                {
                    query = @"
                        UPDATE VideoCallParticipants 
                        SET Status = @Status, LeftAt = NOW(),
                        Duration = TIMESTAMPDIFF(SECOND, JoinedAt, NOW())
                        WHERE CallId = @CallId AND UserId = @UserId";
                }
                else
                {
                    query = @"
                        UPDATE VideoCallParticipants 
                        SET Status = @Status 
                        WHERE CallId = @CallId AND UserId = @UserId";
                }

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Status", status);

                if (status == "Joined" && joinedAt.HasValue)
                {
                    command.Parameters.AddWithValue("@JoinedAt", joinedAt.Value);
                }

                var result = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Updated participant {userId} status to {status} for call {callId}");
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating participant status for {userId} in call {callId}");
                return false;
            }
        }

        public async Task<bool> UpdateParticipantDurationAsync(string callId, int userId, int duration)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
                var query = @"
                    UPDATE VideoCallParticipants 
                    SET Duration = @Duration 
                    WHERE CallId = @CallId AND UserId = @UserId";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Duration", duration);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating participant duration for {userId} in call {callId}");
                return false;
            }
        }

        public async Task<List<VideoCallParticipant>> GetCallParticipantsAsync(string callId)
        {
            var participants = new List<VideoCallParticipant>();

            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
                var query = @"
                    SELECT vcp.*, s.firstname, s.lastname, s.email, s.photo
                    FROM VideoCallParticipants vcp
                    INNER JOIN students s ON vcp.UserId = s.id
                    WHERE vcp.CallId = @CallId
                    ORDER BY vcp.CreatedAt ASC";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    byte[] photoData = null;
                    if (reader["photo"] != DBNull.Value)
                    {
                        photoData = (byte[])reader["photo"];
                    }

                    participants.Add(new VideoCallParticipant
                    {
                        ParticipantId = reader["ParticipantId"].ToString(),
                        CallId = reader["CallId"].ToString(),
                        UserId = Convert.ToInt32(reader["UserId"]),
                        FullName = $"{reader["firstname"]} {reader["lastname"]}",
                        Email = reader["email"].ToString(),
                        PhotoBase64 = photoData != null ? Convert.ToBase64String(photoData) : null,
                        JoinedAt = reader.IsDBNull("JoinedAt") ? null : reader.GetDateTime("JoinedAt"),
                        LeftAt = reader.IsDBNull("LeftAt") ? null : reader.GetDateTime("LeftAt"),
                        Status = reader["Status"].ToString(),
                        Duration = reader.IsDBNull("Duration") ? null : reader.GetInt32("Duration")
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting participants for call {callId}");
            }

            return participants;
        }

        public async Task<VideoCallDetails> GetCallDetailsAsync(string callId)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();

                // Get call basic info
                var callQuery = @"
                    SELECT vch.*, 
                           caller.firstname as caller_firstname, 
                           caller.lastname as caller_lastname,
                           caller.photo as caller_photo
                    FROM VideoCallHistory vch
                    INNER JOIN students caller ON vch.CallerId = caller.id
                    WHERE vch.CallId = @CallId";

                VideoCallDetails callDetails = null;

                using (var command = new NpgsqlCommand(callQuery, connection))
                {
                    command.Parameters.AddWithValue("@CallId", callId);

                    using var reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        byte[] callerPhotoData = null;
                        if (reader["caller_photo"] != DBNull.Value)
                        {
                            callerPhotoData = (byte[])reader["caller_photo"];
                        }

                        callDetails = new VideoCallDetails
                        {
                            CallId = reader["CallId"].ToString(),
                            CallerId = Convert.ToInt32(reader["CallerId"]),
                            CallerName = $"{reader["caller_firstname"]} {reader["caller_lastname"]}",
                            CallerPhotoBase64 = callerPhotoData != null ? Convert.ToBase64String(callerPhotoData) : null,
                            ReceiverType = reader["ReceiverType"].ToString(),
                            ReceiverId = reader["ReceiverId"].ToString(),
                            CallType = reader["CallType"].ToString(),
                            CallStatus = reader["CallStatus"].ToString(),
                            StartTime = reader.GetDateTime("StartTime"),
                            EndTime = reader.IsDBNull("EndTime") ? null : reader.GetDateTime("EndTime"),
                            Duration = reader.IsDBNull("Duration") ? null : reader.GetInt32("Duration"),
                            ParticipantsCount = reader.GetInt32("ParticipantsCount")
                        };
                    }
                }

                if (callDetails != null)
                {
                    // Get participants
                    callDetails.Participants = await GetCallParticipantsAsync(callId);

                    // Get receiver name
                    if (callDetails.ReceiverType == "Group")
                    {
                        callDetails.ReceiverName = "Group Chat";
                    }
                    else
                    {
                        var receiverQuery = "SELECT firstname, lastname FROM students WHERE id = @ReceiverId";
                        using var command = new NpgsqlCommand(receiverQuery, connection);
                        command.Parameters.AddWithValue("@ReceiverId", callDetails.ReceiverId);

                        using var reader = await command.ExecuteReaderAsync();
                        if (await reader.ReadAsync())
                        {
                            callDetails.ReceiverName = $"{reader["firstname"]} {reader["lastname"]}";
                        }
                        else
                        {
                            callDetails.ReceiverName = "Unknown User";
                        }
                    }
                }

                return callDetails;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting call details for {callId}");
                return null;
            }
        }

        public async Task<List<VideoCallDetails>> GetUserCallHistoryAsync(int userId)
        {
            var callHistory = new List<VideoCallDetails>();

            try
            {
                using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();

                // Get calls where user is caller or participant
                var query = @"
                    SELECT DISTINCT vch.CallId
                    FROM VideoCallHistory vch
                    LEFT JOIN VideoCallParticipants vcp ON vch.CallId = vcp.CallId
                    WHERE vch.CallerId = @UserId OR vcp.UserId = @UserId
                    ORDER BY vch.StartTime DESC
                    LIMIT 50";

                var callIds = new List<string>();

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UserId", userId);

                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        callIds.Add(reader["CallId"].ToString());
                    }
                }

                // Get details for each call
                foreach (var callId in callIds)
                {
                    var callDetails = await GetCallDetailsAsync(callId);
                    if (callDetails != null)
                    {
                        callHistory.Add(callDetails);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting call history for user {userId}");
            }

            return callHistory;
        }
    }

    public class VideoCallParticipant
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

    public class VideoCallDetails
    {
        public string CallId { get; set; }
        public int CallerId { get; set; }
        public string CallerName { get; set; }
        public string CallerPhotoBase64 { get; set; }
        public string ReceiverType { get; set; }
        public string ReceiverId { get; set; }
        public string ReceiverName { get; set; }
        public string CallType { get; set; }
        public string CallStatus { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? Duration { get; set; }
        public int ParticipantsCount { get; set; }
        public List<VideoCallParticipant> Participants { get; set; } = new List<VideoCallParticipant>();
    }
}

