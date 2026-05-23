using System.ComponentModel;

namespace AISupportAnalysisPlatform.Enums 
{ 
    public enum AiAnalysisStatus 
    { 
        [Description("NotStarted")] NotStarted, 
        [Description("Queued")] Queued, 
        [Description("InProgress")] InProgress, 
        [Description("Success")] Success, 
        [Description("Failed")] Failed, 
        [Description("Stopped")] Stopped 
    } 
    public enum AiConfidenceLevel { Low, Medium, High } 
    public enum AiRelevanceLevel { Low, Medium, High } 
    public enum AiLogLevel { Info, Warning, Error } 
    public enum AiCustomerSentiment 
    { 
        [Description("Neutral")] Neutral, 
        [Description("Frustrated")] Frustrated, 
        [Description("Angry")] Angry, 
        [Description("Delighted")] Delighted, 
        [Description("Satisfied")] Satisfied, 
        [Description("Indifferent")] Indifferent, 
        [Description("Concerned")] Concerned, 
        [Description("Unhappy")] Unhappy, 
        [Description("Disappointed")] Disappointed, 
        [Description("Confused")] Confused, 
        [Description("Happy")] Happy, 
        [Description("Unknown")] Unknown 
    }
    public enum TicketSortField { Id, Date, Title, Entity, Status, Priority, Category }

    public enum SystemStrings
    {
        [Description("Unknown")] Unknown,
        [Description("General")] General,
        [Description("Unassigned")] Unassigned,
        [Description("None")] None
    }
}
