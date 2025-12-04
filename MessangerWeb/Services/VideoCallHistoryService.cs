using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MessangerWeb.Services
{
    public interface IVideoCallHistoryService
    {
        Task<bool> StartCallAsync(string callId, int callerId, string receiverType, string receiverId, string callType);
        Task<bool> UpdateCallStatusAsync(string callId, string status);
        Task<bool> EndCallAsync(string callId, int duration);
        Task<bool> UpdateCallDurationAsync(string callId, int duration);
        Task<List<VideoCallHistoryItem>> GetCallHistoryAsync(int userId);
        Task<bool> UpdateParticipantsCountAsync(string callId, int count);
    }

    public class VideoCallHistoryService : IVideoCallHistoryService
    {
        private readonly string _connectionString;
        private readonly ILogger<VideoCallHistoryService> _logger;

        public VideoCallHistoryService(IConfiguration configuration, ILogger<VideoCallHistoryService> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _logger = logger;
        }

        public async Task<bool> StartCallAsync(string callId, int callerId, string receiverType, string receiverId, string callType)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    INSERT INTO VideoCallHistory 
                    (CallId, CallerId, ReceiverType, ReceiverId, CallType, CallStatus, StartTime, ParticipantsCount)
                    VALUES 
                    (@CallId, @CallerId, @ReceiverType, @ReceiverId, @CallType, 'Ringing', NOW(), 0)";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@CallerId", callerId);
                command.Parameters.AddWithValue("@ReceiverType", receiverType);
                command.Parameters.AddWithValue("@ReceiverId", receiverId);
                command.Parameters.AddWithValue("@CallType", callType);

                var result = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Started call {callId} from caller {callerId}");
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error starting call {callId}");
                return false;
            }
        }

        public async Task<bool> UpdateCallStatusAsync(string callId, string status)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    UPDATE VideoCallHistory 
                    SET CallStatus = @Status 
                    WHERE CallId = @CallId";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@Status", status);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating call status for {callId}");
                return false;
            }
        }

        public async Task<bool> EndCallAsync(string callId, int duration)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    UPDATE VideoCallHistory 
                    SET CallStatus = 'Completed', EndTime = NOW(), Duration = @Duration 
                    WHERE CallId = @CallId";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@Duration", duration);

                var result = await command.ExecuteNonQueryAsync();
                _logger.LogInformation($"Ended call {callId} with duration {duration}");
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error ending call {callId}");
                return false;
            }
        }

        public async Task<bool> UpdateCallDurationAsync(string callId, int duration)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    UPDATE VideoCallHistory 
                    SET Duration = @Duration 
                    WHERE CallId = @CallId";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@Duration", duration);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating call duration for {callId}");
                return false;
            }
        }

        public async Task<List<VideoCallHistoryItem>> GetCallHistoryAsync(int userId)
        {
            var history = new List<VideoCallHistoryItem>();

            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT vch.*, 
                           caller.firstname as caller_firstname, 
                           caller.lastname as caller_lastname
                    FROM VideoCallHistory vch
                    INNER JOIN students caller ON vch.CallerId = caller.id
                    WHERE vch.CallerId = @UserId OR vch.ReceiverId = @UserId::text
                    ORDER BY vch.StartTime DESC
                    LIMIT 50";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@UserId", userId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    history.Add(new VideoCallHistoryItem
                    {
                        CallId = reader["CallId"].ToString(),
                        CallerId = Convert.ToInt32(reader["CallerId"]),
                        CallerName = $"{reader["caller_firstname"]} {reader["caller_lastname"]}",
                        ReceiverType = reader["ReceiverType"].ToString(),
                        ReceiverId = reader["ReceiverId"].ToString(),
                        CallType = reader["CallType"].ToString(),
                        CallStatus = reader["CallStatus"].ToString(),
                        StartTime = reader.GetDateTime(reader.GetOrdinal("StartTime")),
                        EndTime = reader.IsDBNull(reader.GetOrdinal("EndTime")) ? null : reader.GetDateTime(reader.GetOrdinal("EndTime")),
                        Duration = reader.IsDBNull(reader.GetOrdinal("Duration")) ? null : reader.GetInt32(reader.GetOrdinal("Duration")),
                        ParticipantsCount = reader.GetInt32(reader.GetOrdinal("ParticipantsCount"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting call history for user {userId}");
            }

            return history;
        }

        public async Task<bool> UpdateParticipantsCountAsync(string callId, int count)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    UPDATE VideoCallHistory 
                    SET ParticipantsCount = @Count 
                    WHERE CallId = @CallId";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@CallId", callId);
                command.Parameters.AddWithValue("@Count", count);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating participants count for {callId}");
                return false;
            }
        }
    }

    public class VideoCallHistoryItem
    {
        public string CallId { get; set; }
        public int CallerId { get; set; }
        public string CallerName { get; set; }
        public string ReceiverType { get; set; }
        public string ReceiverId { get; set; }
        public string CallType { get; set; }
        public string CallStatus { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? Duration { get; set; }
        public int ParticipantsCount { get; set; }
    }
}
