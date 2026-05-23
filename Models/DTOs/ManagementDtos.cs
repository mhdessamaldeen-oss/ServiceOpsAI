namespace AISupportAnalysisPlatform.Models.DTOs
{
    public class EntityDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    public class UserDto
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string FullName => $"{FirstName} {LastName}".Trim();
        public int? EntityId { get; set; }
        public string? EntityName { get; set; }
        public bool IsActive { get; set; }
    }

    public class TicketDto
    {
        public int Id { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? EntityName { get; set; }
        public string? CategoryName { get; set; }
        public string? PriorityName { get; set; }
        public string? StatusName { get; set; }
        public bool IsClosedState { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedByUserName { get; set; }
        public string? AssignedToUserName { get; set; }
        public bool IsSlaBreached { get; set; }
        public int AttachmentCount { get; set; }
        public int CommentCount { get; set; }
    }

    public class NotificationDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public bool IsRead { get; set; }
        public string? Link { get; set; }
    }

    public class CopilotToolDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ToolType { get; set; } = string.Empty;
        public string CopilotMode { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string? LastRunAt { get; set; }
    }

    public class ReportMetricDto
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class ReferenceDataDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int? Level { get; set; }
        public bool? IsClosedState { get; set; }
    }
}
