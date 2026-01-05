using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace MessangerWeb.Hubs
{
    public class StudentHub : Hub
    {
        // This method can be called by clients to manually trigger an update if needed,
        // but primarily the server (Controller) will broadcast.
        public async Task NotifyUpdate()
        {
            await Clients.All.SendAsync("ReceiveStudentUpdate");
        }
    }
}
