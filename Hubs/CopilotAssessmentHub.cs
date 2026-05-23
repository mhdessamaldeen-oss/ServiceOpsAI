using Microsoft.AspNetCore.SignalR;

namespace ServiceOpsAI.Hubs
{
    public class CopilotAssessmentHub : Hub
    {
        public async Task JoinSession(string sessionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        }

        public async Task LeaveSession(string sessionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
        }
    }

    public interface ICopilotAssessmentClient
    {
        Task ProgressUpdate(int completedCount, int totalCount, Guid? runId = null);
        Task PhaseUpdate(string status);
        Task AssessmentComplete(Guid runId, object summary);
    }
}
