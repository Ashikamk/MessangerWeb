using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

public class GroupHub : Hub
{
    public async Task JoinGroup(string groupId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"group_{groupId}");
    }

    public async Task LeaveGroup(string groupId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group_{groupId}");
    }

    public async Task NotifyGroupUpdate(string groupId, object updateData)
    {
        await Clients.Group($"group_{groupId}").SendAsync("ReceiveGroupUpdate", updateData);
    }
}