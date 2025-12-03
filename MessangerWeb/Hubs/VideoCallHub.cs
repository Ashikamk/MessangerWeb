using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MessangerWeb.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

public class VideoCallHub : Hub
{
    // Thread-safe dictionaries for managing connections and calls
    private static readonly ConcurrentDictionary<string, string> _userConnections = new();
    private static readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();
    private static readonly ConcurrentDictionary<string, GroupCall> _activeGroupCalls = new();
    private static readonly ConcurrentDictionary<string, string> _userCallStates = new();

    private readonly UserService _userService;
    private readonly ILogger<VideoCallHub> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public VideoCallHub(UserService userService, ILogger<VideoCallHub> logger, IHttpContextAccessor httpContextAccessor)
    {
        _userService = userService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    #region Connection Management

    public override async Task OnConnectedAsync()
    {
        try
        {
            _logger.LogInformation("=== NEW CONNECTION ===");

            var userId = GetUserId();
            _logger.LogInformation("User ID: {UserId}, Connection ID: {ConnectionId}", userId, Context.ConnectionId);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("User not authenticated for connection {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            // Store connection
            _userConnections[userId] = Context.ConnectionId;
            _userService.MarkUserOnline(userId);

            _logger.LogInformation("✅ User {UserId} connected with connection {ConnectionId}", userId, Context.ConnectionId);

            // Notify all clients about online status
            await Clients.All.SendAsync("UserOnlineStatus", userId, true);
            await base.OnConnectedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in OnConnectedAsync for connection {ConnectionId}", Context.ConnectionId);
            Context.Abort();
        }
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            var userId = GetUserId();
            if (!string.IsNullOrEmpty(userId))
            {
                _logger.LogInformation("User {UserId} disconnecting", userId);

                _userConnections.TryRemove(userId, out _);
                _userCallStates.TryRemove(userId, out _);
                _userService.MarkUserOffline(userId);

                await HandleUserDisconnect(userId);
                await Clients.All.SendAsync("UserOnlineStatus", userId, false);
            }

            await base.OnDisconnectedAsync(exception);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync");
        }
    }

    #endregion

    #region Individual Call Initiation

    /// <summary>
    /// Initiates a video call to a receiver
    /// </summary>
    public async Task SendCallOffer(string receiverId, object offerData)
    {
        try
        {
            _logger.LogInformation("=== INITIATING INDIVIDUAL CALL ===");

            if (string.IsNullOrEmpty(receiverId))
            {
                await Clients.Caller.SendAsync("CallError", "Invalid receiver ID");
                return;
            }

            var callerId = GetUserId();
            _logger.LogInformation("Caller: {CallerId} → Receiver: {ReceiverId}", callerId, receiverId);

            if (string.IsNullOrEmpty(callerId))
            {
                await Clients.Caller.SendAsync("CallError", "User not authenticated");
                return;
            }

            // Check if receiver is busy
            if (_userCallStates.TryGetValue(receiverId, out var receiverState) &&
                (receiverState == "in-call" || receiverState == "ringing"))
            {
                _logger.LogWarning("Receiver {ReceiverId} is busy", receiverId);
                await Clients.Caller.SendAsync("UserBusy", receiverId);
                return;
            }

            // Parse offer data
            var json = JsonSerializer.Serialize(offerData);
            var callData = JsonSerializer.Deserialize<CallOfferData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (callData == null || string.IsNullOrEmpty(callData.CallId))
            {
                await Clients.Caller.SendAsync("CallError", "Invalid call data");
                return;
            }

            var callId = callData.CallId;

            // Create active call record
            var activeCall = new ActiveCall
            {
                CallId = callId,
                CallerId = callerId,
                ReceiverId = receiverId,
                CallerName = callData.CallerName ?? "Unknown",
                CallerPhoto = callData.CallerPhoto,
                Offer = callData.Offer,
                Status = "ringing",
                StartTime = DateTime.UtcNow,
                IsGroupCall = false
            };

            _activeCalls[callId] = activeCall;
            _userCallStates[callerId] = "calling";
            _userCallStates[receiverId] = "ringing";

            // Send offer to receiver if online
            if (_userConnections.TryGetValue(receiverId, out var receiverConnectionId))
            {
                _logger.LogInformation("✅ Sending call offer to receiver {ReceiverId}", receiverId);

                await Clients.Client(receiverConnectionId).SendAsync("ReceiveCallOffer", new
                {
                    type = "call-offer",
                    callId = callId,
                    callerId = callerId,
                    callerName = callData.CallerName ?? "Unknown Caller",
                    callerPhoto = callData.CallerPhoto,
                    offer = callData.Offer,
                    timestamp = DateTime.UtcNow,
                    isGroupCall = false
                });

                await Clients.Caller.SendAsync("CallOfferSent", new
                {
                    callId = callId,
                    receiverId = receiverId,
                    status = "ringing"
                });
            }
            else
            {
                _logger.LogWarning("Receiver {ReceiverId} is offline", receiverId);
                await Clients.Caller.SendAsync("UserOffline", receiverId);
                await CleanupCall(callId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in SendCallOffer");
            await Clients.Caller.SendAsync("CallError", $"Failed to initiate call: {ex.Message}");
        }
    }

    #endregion

    #region Group Call Initiation

    /// <summary>
    /// Starts a group video call
    /// </summary>
    public async Task StartGroupCall(string groupId, object groupCallData)
    {
        try
        {
            _logger.LogInformation("=== STARTING GROUP CALL ===");

            var callerId = GetUserId();
            if (string.IsNullOrEmpty(callerId))
            {
                await Clients.Caller.SendAsync("CallError", "User not authenticated");
                return;
            }

            // Parse group call data
            var json = JsonSerializer.Serialize(groupCallData);
            var callData = JsonSerializer.Deserialize<GroupCallOfferData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (callData == null || string.IsNullOrEmpty(callData.CallId))
            {
                await Clients.Caller.SendAsync("CallError", "Invalid group call data");
                return;
            }

            var callId = callData.CallId;

            _logger.LogInformation("Group Call ID: {CallId}, Group: {GroupId}, Caller: {CallerId}",
                callId, groupId, callerId);

            // Create group call record
            var groupCall = new GroupCall
            {
                CallId = callId,
                GroupId = groupId,
                CallerId = callerId,
                CallerName = callData.CallerName ?? "Unknown",
                CallerPhoto = callData.CallerPhoto,
                GroupName = callData.GroupName ?? "Group",
                GroupPhoto = callData.GroupPhoto,
                Offer = callData.Offer,
                Participants = new List<GroupCallParticipant>
                {
                    new GroupCallParticipant
                    {
                        UserId = callerId,
                        UserName = callData.CallerName ?? "You",
                        ConnectionId = Context.ConnectionId,
                        HasJoined = true,
                        JoinedAt = DateTime.UtcNow
                    }
                },
                StartTime = DateTime.UtcNow,
                Status = "active"
            };

            // Add all participants
            foreach (var participantId in callData.ParticipantIds ?? new List<string>())
            {
                if (participantId != callerId) // Don't add caller twice
                {
                    groupCall.Participants.Add(new GroupCallParticipant
                    {
                        UserId = participantId,
                        UserName = "User", // You might want to get actual names from your user service
                        HasJoined = false,
                        InvitedAt = DateTime.UtcNow
                    });
                }
            }

            _activeGroupCalls[callId] = groupCall;
            _userCallStates[callerId] = "in-group-call";

            _logger.LogInformation("Group call created with {ParticipantCount} participants",
                groupCall.Participants.Count);

            // Send invitations to all participants
            var invitedCount = 0;
            foreach (var participant in groupCall.Participants)
            {
                if (participant.UserId != callerId && _userConnections.TryGetValue(participant.UserId, out var connectionId))
                {
                    try
                    {
                        await Clients.Client(connectionId).SendAsync("ReceiveCallOffer", new
                        {
                            type = "group-call-offer",
                            callId = callId,
                            callerId = callerId,
                            callerName = callData.CallerName ?? "Unknown Caller",
                            callerPhoto = callData.CallerPhoto,
                            groupId = groupId,
                            groupName = callData.GroupName ?? "Group",
                            groupPhoto = callData.GroupPhoto,
                            offer = callData.Offer,
                            timestamp = DateTime.UtcNow,
                            isGroupCall = true
                        });

                        _userCallStates[participant.UserId] = "ringing";
                        invitedCount++;
                        _logger.LogInformation("✅ Invited participant {ParticipantId}", participant.UserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to invite participant {ParticipantId}", participant.UserId);
                    }
                }
            }

            // Notify caller that group call started
            await Clients.Caller.SendAsync("GroupCallStarted", new
            {
                callId = callId,
                groupId = groupId,
                groupName = callData.GroupName,
                participantCount = groupCall.Participants.Count,
                invitedCount = invitedCount
            });

            _logger.LogInformation("✅ Group call {CallId} started successfully with {InvitedCount} invitations",
                callId, invitedCount);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in StartGroupCall");
            await Clients.Caller.SendAsync("CallError", $"Failed to start group call: {ex.Message}");
        }
    }

    /// <summary>
    /// Accepts a group call invitation
    /// </summary>
    public async Task AcceptGroupCall(string callId, object answer, string participantId = null)
    {
        try
        {
            _logger.LogInformation("=== ACCEPTING GROUP CALL {CallId} ===", callId);

            var userId = participantId ?? GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("CallError", "User not authenticated");
                return;
            }

            if (!_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                _logger.LogWarning("Group call {CallId} not found", callId);
                await Clients.Caller.SendAsync("CallError", "Group call not found");
                return;
            }

            // Find and update participant
            var participant = groupCall.Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant == null)
            {
                _logger.LogWarning("User {UserId} not invited to group call {CallId}", userId, callId);
                await Clients.Caller.SendAsync("CallError", "Not invited to this group call");
                return;
            }

            participant.HasJoined = true;
            participant.JoinedAt = DateTime.UtcNow;
            participant.ConnectionId = Context.ConnectionId;
            _userCallStates[userId] = "in-group-call";

            _logger.LogInformation("✅ User {UserId} joined group call {CallId}", userId, callId);

            // Notify the call host and other participants
            foreach (var otherParticipant in groupCall.Participants.Where(p => p.HasJoined && p.UserId != userId))
            {
                if (_userConnections.TryGetValue(otherParticipant.UserId, out var connectionId))
                {
                    await Clients.Client(connectionId).SendAsync("GroupCallUpdate", new
                    {
                        callId = callId,
                        updateType = "participantJoined",
                        participantId = userId,
                        participantName = participant.UserName,
                        participantCount = groupCall.Participants.Count(p => p.HasJoined)
                    });
                }
            }

            // Send answer back to the host (first participant who started the call)
            var host = groupCall.Participants.FirstOrDefault(p => p.UserId == groupCall.CallerId);
            if (host != null && _userConnections.TryGetValue(host.UserId, out var hostConnectionId))
            {
                await Clients.Client(hostConnectionId).SendAsync("CallAccepted", new
                {
                    callId = callId,
                    participantId = userId,
                    answer = answer,
                    isGroupCall = true
                });
            }

            await Clients.Caller.SendAsync("GroupCallJoined", new
            {
                callId = callId,
                groupId = groupCall.GroupId,
                groupName = groupCall.GroupName,
                participantCount = groupCall.Participants.Count(p => p.HasJoined)
            });

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting group call {CallId}", callId);
            await Clients.Caller.SendAsync("CallError", "Failed to join group call");
        }
    }

    #endregion

    #region Call Response

    /// <summary>
    /// Accepts an incoming call (both individual and group)
    /// </summary>
    public async Task AcceptCall(string callId, object answer)
    {
        try
        {
            _logger.LogInformation("=== ACCEPTING CALL {CallId} ===", callId);

            var receiverId = GetUserId();
            if (string.IsNullOrEmpty(receiverId))
            {
                await Clients.Caller.SendAsync("CallError", "User not authenticated");
                return;
            }

            // Check if it's a group call
            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                await AcceptGroupCall(callId, answer, receiverId);
                return;
            }

            // Handle individual call
            if (!_activeCalls.TryGetValue(callId, out var call) || call.ReceiverId != receiverId)
            {
                _logger.LogWarning("Call {CallId} not found or unauthorized", callId);
                await Clients.Caller.SendAsync("CallError", "Call not found");
                return;
            }

            // Update call status
            call.Status = "accepted";
            call.Answer = answer;
            call.AcceptedAt = DateTime.UtcNow;

            _userCallStates[call.CallerId] = "in-call";
            _userCallStates[receiverId] = "in-call";

            // Notify caller
            if (_userConnections.TryGetValue(call.CallerId, out var callerConnectionId))
            {
                _logger.LogInformation("✅ Notifying caller about call acceptance");

                await Clients.Client(callerConnectionId).SendAsync("CallAccepted", new
                {
                    callId = callId,
                    receiverId = receiverId,
                    answer = answer,
                    isGroupCall = false
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting call {CallId}", callId);
            await Clients.Caller.SendAsync("CallError", "Failed to accept call");
        }
    }

    /// <summary>
    /// Rejects an incoming call
    /// </summary>
    public async Task RejectCall(string callId, string reason = "Call declined")
    {
        try
        {
            _logger.LogInformation("=== REJECTING CALL {CallId} ===", callId);

            var receiverId = GetUserId();
            if (string.IsNullOrEmpty(receiverId)) return;

            // Check if it's a group call
            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                var participant = groupCall.Participants.FirstOrDefault(p => p.UserId == receiverId);
                if (participant != null)
                {
                    // For group calls, just remove the participant from ringing state
                    _userCallStates.TryRemove(receiverId, out _);

                    // Notify host
                    if (_userConnections.TryGetValue(groupCall.CallerId, out var hostConnectionId))
                    {
                        await Clients.Client(hostConnectionId).SendAsync("GroupCallUpdate", new
                        {
                            callId = callId,
                            updateType = "participantRejected",
                            participantId = receiverId,
                            participantName = participant.UserName,
                            reason = reason
                        });
                    }
                }
                return;
            }

            // Handle individual call rejection
            if (_activeCalls.TryGetValue(callId, out var call) && call.ReceiverId == receiverId)
            {
                call.Status = "rejected";

                if (_userConnections.TryGetValue(call.CallerId, out var callerConnectionId))
                {
                    await Clients.Client(callerConnectionId).SendAsync("CallRejected", new
                    {
                        callId = callId,
                        reason = reason,
                        receiverId = receiverId
                    });
                }

                await CleanupCall(callId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting call {CallId}", callId);
        }
    }

    /// <summary>
    /// Cancels an outgoing call before it's answered
    /// </summary>
    public async Task CancelCall(string callId)
    {
        try
        {
            _logger.LogInformation("=== CANCELING CALL {CallId} ===", callId);

            var callerId = GetUserId();
            if (string.IsNullOrEmpty(callerId)) return;

            if (_activeCalls.TryGetValue(callId, out var call) && call.CallerId == callerId)
            {
                if (_userConnections.TryGetValue(call.ReceiverId, out var receiverConnectionId))
                {
                    await Clients.Client(receiverConnectionId).SendAsync("CallCancelled", new
                    {
                        callId = callId,
                        callerId = callerId
                    });
                }

                await CleanupCall(callId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling call {CallId}", callId);
        }
    }

    #endregion

    #region Call Termination

    /// <summary>
    /// Ends an active call (both individual and group)
    /// </summary>
    public async Task EndCall(string callId)
    {
        try
        {
            _logger.LogInformation("=== ENDING CALL {CallId} ===", callId);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return;

            // Check if it's a group call
            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                await EndGroupCall(callId, userId);
                return;
            }

            // Handle individual call ending
            if (_activeCalls.TryGetValue(callId, out var call))
            {
                // Calculate duration
                var duration = (DateTime.UtcNow - call.StartTime).TotalSeconds;
                var formattedDuration = FormatCallDuration(duration);

                // Remove call first to prevent loops
                _activeCalls.TryRemove(callId, out _);

                // Notify the other participant
                var otherUserId = userId == call.CallerId ? call.ReceiverId : call.CallerId;

                if (_userConnections.TryGetValue(otherUserId, out var otherUserConnectionId))
                {
                    await Clients.Client(otherUserConnectionId).SendAsync("CallEnded", new
                    {
                        callId = callId,
                        endedBy = userId,
                        duration = duration,
                        formattedDuration = formattedDuration,
                        reason = "Call ended"
                    });
                }

                // Clean up states
                _userCallStates.TryRemove(call.CallerId, out _);
                _userCallStates.TryRemove(call.ReceiverId, out _);

                _logger.LogInformation("✅ Individual call {CallId} ended successfully", callId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending call {CallId}", callId);
        }
    }

    /// <summary>
    /// Ends a group call
    /// </summary>
    public async Task EndGroupCall(string callId, string groupId = null)
    {
        try
        {
            _logger.LogInformation("=== ENDING GROUP CALL {CallId} ===", callId);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return;

            if (_activeGroupCalls.TryRemove(callId, out var groupCall))
            {
                var duration = (DateTime.UtcNow - groupCall.StartTime).TotalSeconds;
                var formattedDuration = FormatCallDuration(duration);

                // Notify all participants
                foreach (var participant in groupCall.Participants.Where(p => p.HasJoined))
                {
                    if (_userConnections.TryGetValue(participant.UserId, out var connectionId))
                    {
                        await Clients.Client(connectionId).SendAsync("GroupCallEnded", new
                        {
                            callId = callId,
                            endedBy = userId,
                            groupId = groupCall.GroupId,
                            groupName = groupCall.GroupName,
                            duration = duration,
                            formattedDuration = formattedDuration,
                            reason = "Group call ended",
                            participantCount = groupCall.Participants.Count(p => p.HasJoined)
                        });
                    }

                    // Clean up user state
                    _userCallStates.TryRemove(participant.UserId, out _);
                }

                _logger.LogInformation("✅ Group call {CallId} ended with {ParticipantCount} participants",
                    callId, groupCall.Participants.Count(p => p.HasJoined));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending group call {CallId}", callId);
        }
    }

    /// <summary>
    /// Leaves a group call without ending it for everyone
    /// </summary>
    public async Task LeaveGroupCall(string callId)
    {
        try
        {
            _logger.LogInformation("=== LEAVING GROUP CALL {CallId} ===", callId);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return;

            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                var participant = groupCall.Participants.FirstOrDefault(p => p.UserId == userId);
                if (participant != null)
                {
                    groupCall.Participants.Remove(participant);
                    _userCallStates.TryRemove(userId, out _);

                    // Notify other participants
                    foreach (var otherParticipant in groupCall.Participants.Where(p => p.HasJoined))
                    {
                        if (_userConnections.TryGetValue(otherParticipant.UserId, out var connectionId))
                        {
                            await Clients.Client(connectionId).SendAsync("GroupCallUpdate", new
                            {
                                callId = callId,
                                updateType = "participantLeft",
                                participantId = userId,
                                participantName = participant.UserName,
                                participantCount = groupCall.Participants.Count(p => p.HasJoined)
                            });
                        }
                    }

                    _logger.LogInformation("✅ User {UserId} left group call {CallId}", userId, callId);

                    // If no participants left or only host remains, end the call
                    if (groupCall.Participants.Count(p => p.HasJoined) <= 1)
                    {
                        await EndGroupCall(callId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leaving group call {CallId}", callId);
        }
    }

    #endregion

    #region WebRTC Signaling

    /// <summary>
    /// Exchanges ICE candidates between peers
    /// </summary>
    public async Task SendICECandidate(string callId, object candidate)
    {
        try
        {
            var senderId = GetUserId();
            if (string.IsNullOrEmpty(senderId)) return;

            _logger.LogInformation("Sending ICE candidate for call {CallId}", callId);

            // Check if it's a group call
            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                // In a real implementation, you'd need to handle group call ICE candidates differently
                // For now, we'll send to the host
                if (_userConnections.TryGetValue(groupCall.CallerId, out var hostConnectionId))
                {
                    await Clients.Client(hostConnectionId).SendAsync("ReceiveICECandidate", new
                    {
                        callId = callId,
                        candidate = candidate,
                        senderId = senderId
                    });
                }
                return;
            }

            // Handle individual call ICE candidates
            if (_activeCalls.TryGetValue(callId, out var call))
            {
                var targetUserId = senderId == call.CallerId ? call.ReceiverId : call.CallerId;

                if (_userConnections.TryGetValue(targetUserId, out var connectionId))
                {
                    await Clients.Client(connectionId).SendAsync("ReceiveICECandidate", new
                    {
                        callId = callId,
                        candidate = candidate,
                        senderId = senderId
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending ICE candidate");
        }
    }

    /// <summary>
    /// Handles media status changes (mute/unmute, camera on/off)
    /// </summary>
    public async Task SendMediaStatus(string callId, string mediaType, bool isEnabled)
    {
        try
        {
            var senderId = GetUserId();
            if (string.IsNullOrEmpty(senderId)) return;

            _logger.LogInformation("User {UserId} {Action} {MediaType}",
                senderId, isEnabled ? "enabled" : "disabled", mediaType);

            // Check if it's a group call
            if (_activeGroupCalls.TryGetValue(callId, out var groupCall))
            {
                // Notify all other participants in the group call
                foreach (var participant in groupCall.Participants.Where(p => p.HasJoined && p.UserId != senderId))
                {
                    if (_userConnections.TryGetValue(participant.UserId, out var connectionId))
                    {
                        await Clients.Client(connectionId).SendAsync("UserMediaStatus", new
                        {
                            callId = callId,
                            userId = senderId,
                            mediaType = mediaType,
                            enabled = isEnabled
                        });
                    }
                }
                return;
            }

            // Handle individual call media status
            if (_activeCalls.TryGetValue(callId, out var call))
            {
                var targetUserId = senderId == call.CallerId ? call.ReceiverId : call.CallerId;

                if (_userConnections.TryGetValue(targetUserId, out var connectionId))
                {
                    await Clients.Client(connectionId).SendAsync("UserMediaStatus", new
                    {
                        callId = callId,
                        userId = senderId,
                        mediaType = mediaType,
                        enabled = isEnabled
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending media status");
        }
    }

    #endregion

    #region Helper Methods

    private string GetUserId()
    {
        try
        {
            // Try Context.User first
            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                var userId = Context.User.FindFirst("UserId")?.Value;
                if (!string.IsNullOrEmpty(userId)) return userId;
            }

            // Try HttpContext
            if (_httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                var userId = _httpContextAccessor.HttpContext.User.FindFirst("UserId")?.Value;
                if (!string.IsNullOrEmpty(userId)) return userId;
            }

            // Try Session
            var sessionUserId = _httpContextAccessor.HttpContext?.Session?.GetString("UserId");
            if (!string.IsNullOrEmpty(sessionUserId)) return sessionUserId;

            _logger.LogError("Could not retrieve UserId for connection {ConnectionId}", Context.ConnectionId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting UserId");
            return null;
        }
    }

    private async Task HandleUserDisconnect(string userId)
    {
        try
        {
            // Handle individual calls
            var userCalls = _activeCalls.Where(c =>
                c.Value.CallerId == userId || c.Value.ReceiverId == userId).ToList();

            foreach (var call in userCalls)
            {
                if (_activeCalls.TryGetValue(call.Key, out var activeCall))
                {
                    var otherUserId = activeCall.CallerId == userId ? activeCall.ReceiverId : activeCall.CallerId;

                    if (_userConnections.TryGetValue(otherUserId, out var otherUserConnectionId))
                    {
                        await Clients.Client(otherUserConnectionId).SendAsync("CallEnded", new
                        {
                            callId = call.Key,
                            endedBy = userId,
                            reason = "User disconnected",
                            duration = (DateTime.UtcNow - activeCall.StartTime).TotalSeconds
                        });
                    }

                    await CleanupCall(call.Key);
                }
            }

            // Handle group calls
            var userGroupCalls = _activeGroupCalls.Where(g =>
                g.Value.Participants.Any(p => p.UserId == userId)).ToList();

            foreach (var groupCall in userGroupCalls)
            {
                await LeaveGroupCall(groupCall.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling user disconnect");
        }
    }

    private async Task CleanupCall(string callId)
    {
        if (_activeCalls.TryRemove(callId, out var call))
        {
            _userCallStates.TryRemove(call.CallerId, out _);
            _userCallStates.TryRemove(call.ReceiverId, out _);
            _logger.LogInformation("Cleaned up individual call {CallId}", callId);
        }
    }

    private string FormatCallDuration(double totalSeconds)
    {
        var seconds = (int)totalSeconds;
        if (seconds < 60) return $"{seconds} sec";

        if (seconds < 3600)
        {
            var minutes = seconds / 60;
            var remainingSeconds = seconds % 60;
            return remainingSeconds > 0 ? $"{minutes} min {remainingSeconds} sec" : $"{minutes} min";
        }

        var hours = seconds / 3600;
        var minutes2 = (seconds % 3600) / 60;
        return minutes2 > 0 ? $"{hours} hr {minutes2} min" : $"{hours} hr";
    }

    #endregion
}

#region Data Models

public class ActiveCall
{
    public string CallId { get; set; }
    public string CallerId { get; set; }
    public string ReceiverId { get; set; }
    public string CallerName { get; set; }
    public string CallerPhoto { get; set; }
    public object Offer { get; set; }
    public object Answer { get; set; }
    public string Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public bool IsGroupCall { get; set; }
}

public class GroupCall
{
    public string CallId { get; set; }
    public string GroupId { get; set; }
    public string CallerId { get; set; }
    public string CallerName { get; set; }
    public string CallerPhoto { get; set; }
    public string GroupName { get; set; }
    public string GroupPhoto { get; set; }
    public object Offer { get; set; }
    public List<GroupCallParticipant> Participants { get; set; } = new List<GroupCallParticipant>();
    public DateTime StartTime { get; set; }
    public string Status { get; set; }
}

public class GroupCallParticipant
{
    public string UserId { get; set; }
    public string UserName { get; set; }
    public string ConnectionId { get; set; }
    public bool HasJoined { get; set; }
    public DateTime? InvitedAt { get; set; }
    public DateTime? JoinedAt { get; set; }
}

public class CallOfferData
{
    public string CallId { get; set; }
    public string CallerName { get; set; }
    public string CallerPhoto { get; set; }
    public object Offer { get; set; }
}

public class GroupCallOfferData
{
    public string CallId { get; set; }
    public string CallerName { get; set; }
    public string CallerPhoto { get; set; }
    public string GroupName { get; set; }
    public string GroupPhoto { get; set; }
    public object Offer { get; set; }
    public List<string> ParticipantIds { get; set; } = new List<string>();
}

#endregion