using Microsoft.AspNetCore.SignalR;

namespace ServiceOpsAI.Hubs
{
    public class CopilotChatHub : Hub
    {
        public async Task JoinChat(string sessionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_{sessionId}");
        }

        public async Task LeaveChat(string sessionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat_{sessionId}");
        }
    }

    public interface ICopilotChatClient
    {
        Task ProgressUpdate(string status);
        Task StepStarted(string stepName);
        Task ChatComplete(object response);
    }
}
