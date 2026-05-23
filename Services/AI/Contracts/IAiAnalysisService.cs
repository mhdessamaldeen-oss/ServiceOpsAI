using AISupportAnalysisPlatform.Models;

namespace AISupportAnalysisPlatform.Services.AI;

public interface IAiAnalysisService
{
    Task<TicketAiAnalysis> RunTicketAnalysisAsync(int ticketId, string userId);
    Task<TicketAiAnalysis> RefreshTicketAnalysisAsync(int ticketId, string userId);
    Task<TicketAiAnalysis?> GetLatestTicketAnalysisAsync(int ticketId);
    Task<List<TicketAiAnalysis>> GetRunHistoryAsync(int ticketId);
    Task<TicketAiAnalysis?> GetAnalysisByRunAsync(int ticketId, int runNumber);
    Task ResetInterruptedAnalysesAsync();
}
