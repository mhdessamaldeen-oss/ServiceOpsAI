using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    public class Ticket
    {
        public int Id { get; set; }

        [StringLength(20)]
        public string TicketNumber { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty;

        public int CategoryId { get; set; }
        public TicketCategory? Category { get; set; }

        public int PriorityId { get; set; }
        public TicketPriority? Priority { get; set; }

        public int StatusId { get; set; }
        public TicketStatus? Status { get; set; }

        public int SourceId { get; set; }
        public TicketSource? Source { get; set; }

        public int? EntityId { get; set; }
        public Entity? Entity { get; set; }

        public string? AssignedToUserId { get; set; }
        public ApplicationUser? AssignedToUser { get; set; }

        public string CreatedByUserId { get; set; } = string.Empty;
        public ApplicationUser? CreatedByUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DueDate { get; set; }

        [StringLength(100)]
        public string? ProductArea { get; set; }

        [StringLength(50)]
        public string? EnvironmentName { get; set; }

        [StringLength(80)]
        public string? BrowserName { get; set; }

        [StringLength(80)]
        public string? OperatingSystem { get; set; }

        [StringLength(120)]
        public string? ExternalReferenceId { get; set; }

        [StringLength(120)]
        public string? ExternalSystemName { get; set; }

        [StringLength(50)]
        public string? ImpactScope { get; set; }

        [Range(1, int.MaxValue)]
        public int? AffectedUsersCount { get; set; }

        [StringLength(2000)]
        public string? TechnicalAssessment { get; set; }

        [StringLength(50)]
        public string? EscalationLevel { get; set; }

        public DateTime? EscalatedAt { get; set; }

        public string? EscalatedToUserId { get; set; }
        public ApplicationUser? EscalatedToUser { get; set; }

        public DateTime? FirstResponseDueAt { get; set; }
        public DateTime? ResolutionDueAt { get; set; }
        public DateTime? FirstRespondedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool IsSlaBreached { get; set; }

        public bool IsDeleted { get; set; }

        [StringLength(500)]
        public string? ResolutionSummary { get; set; }

        public string? ResolvedByUserId { get; set; }
        public ApplicationUser? ResolvedByUser { get; set; }

        [StringLength(1500)]
        public string? RootCause { get; set; }

        [StringLength(1500)]
        public string? VerificationNotes { get; set; }

        public string? ResolutionApprovedByUserId { get; set; }
        public ApplicationUser? ResolutionApprovedByUser { get; set; }

        public DateTime? ResolutionApprovedAt { get; set; }

        [StringLength(500)]
        public string? PendingReason { get; set; }

        public bool RequiresManagerReview { get; set; }

        public ICollection<TicketComment> Comments { get; set; } = new List<TicketComment>();
        public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
        public ICollection<TicketHistory> HistoryRecords { get; set; } = new List<TicketHistory>();

        // Parent and Child Tickets relationship
        public int? ParentTicketId { get; set; }
        public Ticket? ParentTicket { get; set; }
        public ICollection<Ticket> ChildTickets { get; set; } = new List<Ticket>();
    }

    public class TicketComment
    {
        public int Id { get; set; }

        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }

        [Required]
        public string Content { get; set; } = string.Empty;

        [Required]
        public string CreatedByUserId { get; set; } = string.Empty;
        public ApplicationUser? CreatedByUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int? CommentAttachmentId { get; set; }
        public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
    }

    public class TicketAttachment
    {
        public int Id { get; set; }

        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        [StringLength(500)]
        public string FilePath { get; set; } = string.Empty;

        [StringLength(100)]
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }

        public int? CommentId { get; set; }
        public TicketComment? Comment { get; set; }

        [Required]
        public string UploadedByUserId { get; set; } = string.Empty;
        public ApplicationUser? UploadedByUser { get; set; }

        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }

    public class TicketHistory
    {
        public int Id { get; set; }

        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }

        [Required]
        public string Action { get; set; } = string.Empty;

        public string? OldValue { get; set; }
        public string? NewValue { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
