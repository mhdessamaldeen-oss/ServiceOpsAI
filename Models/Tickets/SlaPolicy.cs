using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Service-Level Agreement policy — per (CustomerSegment × Priority × ServiceType).
/// Today SLA is implicit in hardcoded Ticket fields (FirstResponseDueAt / ResolutionDueAt).
/// Externalising it as a policy table lets the Copilot answer "what is the response
/// SLA for industrial electricity P2 tickets" and "how many tickets breached SLA in
/// each segment last quarter" — and lets the business edit SLAs without code changes.
/// </summary>
public class SlaPolicy
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string PolicyCode { get; set; } = string.Empty;     // "RES-ELEC-P1"

    [Required, StringLength(150)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string NameAr { get; set; } = string.Empty;

    public int? CustomerSegmentId { get; set; }
    public CustomerSegment? CustomerSegment { get; set; }

    public int? ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int? PriorityId { get; set; }
    public TicketPriority? Priority { get; set; }

    /// <summary>Minutes from ticket creation to required first response.</summary>
    public int FirstResponseMinutes { get; set; }

    /// <summary>Minutes from ticket creation to required resolution.</summary>
    public int ResolutionMinutes { get; set; }

    /// <summary>Whether the SLA clock pauses outside business hours / holidays.</summary>
    public bool BusinessHoursOnly { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }

    public bool IsActive { get; set; } = true;

    [StringLength(500)]
    public string? Notes { get; set; }
}
