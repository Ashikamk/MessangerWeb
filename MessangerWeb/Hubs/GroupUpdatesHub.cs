using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

public class GroupUpdatesHub : Hub
{
    // This method can be called after a successful group edit.
    public async Task BroadcastGroupUpdate(string groupId, string groupName, string groupImageBase64)
    {
        // Send the update to all members currently connected to this group's "channel".
        await Clients.Group($"group-{groupId}").SendAsync("ReceiveGroupUpdate", groupId, groupName, groupImageBase64);
    }

    // Automatically join a user to the group's notification channel when they connect.
    public override async Task OnConnectedAsync()
    {
        // You can pass the groupId via query string when the connection starts.
        var httpContext = Context.GetHttpContext();
        if (httpContext.Request.Query.TryGetValue("groupId", out var groupIdValue))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"group-{groupIdValue}");
        }
        await base.OnConnectedAsync();
    }
}