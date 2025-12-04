// Services/VideoCallHistoryService.cs
using System.Data;
using MessangerWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using MessangerWeb.Models;

namespace WebsiteApplication.Services
{
    public interface IVideoCallHistoryService
    {
        Task<string> StartCallAsync(string callerId, string receiverId, string receiverType, string callType);
        Task<bool> UpdateCallStatusAsync(string callId, string status);
        Task<bool> EndCallAsync(string callId, string status = "Completed");
        Task<List<VideoCallHistory>> GetCallHistoryAsync(string userId);
        Task<VideoCallHistory> GetCallByIdAsync(string callId);
        Task<bool> UpdateParticipantsCountAsync(string callId, int participantsCount);
        Task<bool> UpdateCallDurationAsync(string callId, int duration);
        Task<bool> UpdateCallWithDurationAsync(string callId, string status, int duration);
    }

    public class VideoCallHistoryService : IVideoCallHistoryService
    {
        private readonly MySqlConnectionService _connectionService;
        private readonly ILogger<VideoCallHistoryService> _logger;
        private readonly string _connectionString;

        public VideoCallHistoryService(MySqlConnectionService connectionService, ILogger<VideoCallHistoryService> logger, IConfiguration configuration)
        {
            _connectionService = connectionService;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? "server=localhost;database=riyoceli_employees_management;uid=riyoceli_employees_management;pwd=2&6u4TWMsFU&;";
        }

        public async Task<string> StartCallAsync(string callerId, string receiverId, string receiverType, string callType)
        {
            var callId = Guid.NewGuid().ToString();

            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    INSERT INTO VideoCallHistory 
                    (CallId, CallerId, ReceiverId, ReceiverType, CallType, CallStatus, StartTime, ParticipantsCount)
                    VALUES 
                    (@CallId, @CallerId, @ReceiverId, @ReceiverType, @CallType, @CallStatus, @StartTime, @ParticipantsCount)";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@CallerId", callerId);
                command.Parameters.AddWithValue("@ReceiverId", receiverId);
                command.Parameters.AddWithValue("@ReceiverType", receiverType);
                command.Parameters.AddWithValue("@CallType", callType);
                command.Parameters.AddWithValue("@CallStatus", "Initiated");
                command.Parameters.AddWithValue("@StartTime", DateTime.UtcNow);
                command.Parameters.AddWithValue("@ParticipantsCount", receiverType == "User" ? 2 : 1);

                await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Call started: {callId}");
                return callId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting call history");
                throw;
            }
        }

        public async Task<bool> UpdateCallStatusAsync(string callId, string status)
        {
            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    UPDATE VideoCallHistory 
                    SET CallStatus = @CallStatus, 
                        EndTime = CASE WHEN @CallStatus IN ('Completed', 'Failed', 'Missed', 'Rejected') THEN @EndTime ELSE EndTime END,
                        Duration = CASE WHEN @CallStatus IN ('Completed', 'Failed') THEN TIMESTAMPDIFF(SECOND, StartTime, @EndTime) ELSE Duration END
                    WHERE CallId = @CallId";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@CallStatus", status);
                command.Parameters.AddWithValue("@EndTime", DateTime.UtcNow);

                var rowsAffected = await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Call {callId} status updated to {status}");
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating call status for {callId}");
                return false;
            }
        }

        public async Task<bool> EndCallAsync(string callId, string status = "Completed")
        {
            return await UpdateCallStatusAsync(callId, status);
        }

        public async Task<bool> UpdateCallDurationAsync(string callId, int duration)
        {
            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    UPDATE VideoCallHistory 
                    SET Duration = @Duration,
                        EndTime = CASE WHEN Duration IS NULL THEN @EndTime ELSE EndTime END
                    WHERE CallId = @CallId";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@Duration", duration);
                command.Parameters.AddWithValue("@EndTime", DateTime.UtcNow);

                var rowsAffected = await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Call {callId} duration updated to {duration} seconds");
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating call duration for {callId}");
                return false;
            }
        }

        public async Task<bool> UpdateCallWithDurationAsync(string callId, string status, int duration)
        {
            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    UPDATE VideoCallHistory 
                    SET CallStatus = @CallStatus,
                        Duration = @Duration,
                        EndTime = @EndTime
                    WHERE CallId = @CallId";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@CallStatus", status);
                command.Parameters.AddWithValue("@Duration", duration);
                command.Parameters.AddWithValue("@EndTime", DateTime.UtcNow);

                var rowsAffected = await command.ExecuteNonQueryAsync();

                _logger.LogInformation($"Call {callId} updated with status {status} and duration {duration} seconds");
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating call with duration for {callId}");
                return false;
            }
        }

        public async Task<List<VideoCallHistory>> GetCallHistoryAsync(string userId)
        {
            var callHistory = new List<VideoCallHistory>();

            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    SELECT vch.CallId, vch.CallerId, vch.ReceiverId, vch.ReceiverType, vch.CallType, vch.CallStatus, 
                           vch.StartTime, vch.EndTime, vch.Duration, vch.ParticipantsCount, vch.CreatedAt,
                           caller.firstname as CallerFirstName, caller.lastname as CallerLastName,
                           receiver.firstname as ReceiverFirstName, receiver.lastname as ReceiverLastName
                    FROM VideoCallHistory vch
                    LEFT JOIN students caller ON vch.CallerId = caller.id
                    LEFT JOIN students receiver ON vch.ReceiverId = receiver.id
                    WHERE vch.CallerId = @UserId OR vch.ReceiverId = @UserId
                    ORDER BY vch.StartTime DESC";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@UserId", userId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    callHistory.Add(new VideoCallHistory
                    {
                        CallId = reader.GetString("CallId"),
                        CallerId = reader.GetString("CallerId"),
                        ReceiverId = reader.GetString("ReceiverId"),
                        ReceiverType = reader.GetString("ReceiverType"),
                        CallType = reader.GetString("CallType"),
                        CallStatus = reader.GetString("CallStatus"),
                        StartTime = reader.GetDateTime("StartTime"),
                        EndTime = reader.IsDBNull("EndTime") ? null : reader.GetDateTime("EndTime"),
                        Duration = reader.IsDBNull("Duration") ? null : reader.GetInt32("Duration"),
                        ParticipantsCount = reader.GetInt32("ParticipantsCount"),
                        CreatedAt = reader.GetDateTime("CreatedAt"),
                        CallerName = $"{reader["CallerFirstName"]} {reader["CallerLastName"]}",
                        ReceiverName = $"{reader["ReceiverFirstName"]} {reader["ReceiverLastName"]}"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting call history for user {userId}");
            }

            return callHistory;
        }

        public async Task<VideoCallHistory> GetCallByIdAsync(string callId)
        {
            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = @"
                    SELECT vch.CallId, vch.CallerId, vch.ReceiverId, vch.ReceiverType, vch.CallType, vch.CallStatus, 
                           vch.StartTime, vch.EndTime, vch.Duration, vch.ParticipantsCount, vch.CreatedAt,
                           caller.firstname as CallerFirstName, caller.lastname as CallerLastName,
                           receiver.firstname as ReceiverFirstName, receiver.lastname as ReceiverLastName
                    FROM VideoCallHistory vch
                    LEFT JOIN students caller ON vch.CallerId = caller.id
                    LEFT JOIN students receiver ON vch.ReceiverId = receiver.id
                    WHERE vch.CallId = @CallId";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new VideoCallHistory
                    {
                        CallId = reader.GetString("CallId"),
                        CallerId = reader.GetString("CallerId"),
                        ReceiverId = reader.GetString("ReceiverId"),
                        ReceiverType = reader.GetString("ReceiverType"),
                        CallType = reader.GetString("CallType"),
                        CallStatus = reader.GetString("CallStatus"),
                        StartTime = reader.GetDateTime("StartTime"),
                        EndTime = reader.IsDBNull("EndTime") ? null : reader.GetDateTime("EndTime"),
                        Duration = reader.IsDBNull("Duration") ? null : reader.GetInt32("Duration"),
                        ParticipantsCount = reader.GetInt32("ParticipantsCount"),
                        CreatedAt = reader.GetDateTime("CreatedAt"),
                        CallerName = $"{reader["CallerFirstName"]} {reader["CallerLastName"]}",
                        ReceiverName = $"{reader["ReceiverFirstName"]} {reader["ReceiverLastName"]}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting call by ID: {callId}");
            }

            return null;
        }

        public async Task<bool> UpdateParticipantsCountAsync(string callId, int participantsCount)
        {
            try
            {
                using var connection = await _connectionService.GetConnectionAsync();
                var query = "UPDATE VideoCallHistory SET ParticipantsCount = @ParticipantsCount WHERE CallId = @CallId";

                using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@ParticipantsCount", participantsCount);

                var rowsAffected = await command.ExecuteNonQueryAsync();
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating participants count for call {callId}");
                return false;
            }
        }
    }
}


public class VideoCallHistory
{
    public string CallId { get; set; }
    public string CallerId { get; set; }
    public string ReceiverId { get; set; }
    public string ReceiverType { get; set; }
    public string CallType { get; set; }
    public string CallStatus { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Duration { get; set; }
    public int ParticipantsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CallerName { get; set; }
    public string ReceiverName { get; set; }

    // Helper property to get formatted duration
    public string FormattedDuration
    {
        get
        {
            if (!Duration.HasValue) return "0 sec";

            var duration = Duration.Value;
            if (duration < 60)
            {
                return $"{duration} sec";
            }
            else if (duration < 3600)
            {
                var minutes = duration / 60;
                var seconds = duration % 60;
                return seconds > 0 ? $"{minutes} min {seconds} sec" : $"{minutes} min";
            }
            else
            {
                var hours = duration / 3600;
                var minutes = (duration % 3600) / 60;
                return minutes > 0 ? $"{hours} hr {minutes} min" : $"{hours} hr";
            }
        }
    }

    // Helper property to get local start time
    public DateTime LocalStartTime => StartTime.ToLocalTime();

    // Helper property to get local end time
    public DateTime? LocalEndTime => EndTime?.ToLocalTime();
}
