using ServiceOpsAI.Models;
using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Models.Common;

public class DashboardViewModel
{
    public int TotalTickets { get; init; }
    public int OpenTickets { get; init; }
    public int ResolvedTickets { get; init; }
    public int ClosedTickets { get; init; }
    public int HighPriorityTickets { get; init; }
    public int TicketsCreatedToday { get; init; }
    public int TicketsCreatedThisWeek { get; init; }
    public int SlaBreachedTickets { get; init; }
    public int PendingTickets { get; init; }
    public int EscalatedTickets { get; init; }
    public double ResolutionRatePercent { get; init; }
    public double SlaRiskPercent { get; init; }
    public CopilotAssessmentRunSummaryDto? LatestCopilotAssessment { get; init; }
    public IReadOnlyList<Ticket> RecentTickets { get; init; } = [];
    public IReadOnlyList<DashboardMetricItem> StatusBreakdown { get; init; } = [];
    public IReadOnlyList<DashboardMetricItem> SourceBreakdown { get; init; } = [];
    public IReadOnlyList<DashboardMetricItem> WeeklyVolume { get; init; } = [];
    public IReadOnlyList<DashboardMetricItem> ProductAreas { get; init; } = [];
}

public class DashboardMetricItem
{
    public required string Label { get; init; }
    public int Value { get; init; }
}
