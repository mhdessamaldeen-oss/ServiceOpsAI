using AISupportAnalysisPlatform.Constants;

namespace AISupportAnalysisPlatform.Helpers
{
    public static class UIExtensions
    {
        public static string GetStatusBadgeClass(string? statusName)
        {
            if (string.IsNullOrEmpty(statusName)) return "status-pending";

            return statusName switch
            {
                TicketStatusNames.New => "status-new",
                TicketStatusNames.Open => "status-open",
                TicketStatusNames.InProgress => "status-inprogress",
                TicketStatusNames.Pending => "status-pending",
                TicketStatusNames.Resolved => "status-resolved",
                TicketStatusNames.Closed => "status-closed",
                TicketStatusNames.Rejected => "status-rejected",
                _ => "status-pending"
            };
        }

        public static string GetPriorityBadgeClass(string? priorityName)
        {
            if (string.IsNullOrEmpty(priorityName)) return "priority-medium";

            return priorityName switch
            {
                TicketPriorityNames.Low => "priority-low",
                TicketPriorityNames.Medium => "priority-medium",
                TicketPriorityNames.High => "priority-high",
                TicketPriorityNames.Critical => "priority-critical",
                _ => "priority-medium"
            };
        }

        public static string GetCategoryBadgeClass(string? categoryName)
        {
            return "category-badge"; // For now all categories use the primary theme color
        }
    }
}
