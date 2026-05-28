using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A dispatched field job. Separate from Ticket — a ticket is a customer complaint;
/// a work order is the operational task to fix it (or a preventive task with no ticket
/// at all). One ticket can spawn multiple work orders, and one outage can require many.
/// Captures dispatch, on-site arrival, completion times so the Copilot can answer
/// "average dispatch-to-arrival time by district" and "preventive vs reactive WO mix".
/// </summary>
public class WorkOrder
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    public string OrderNumber { get; set; } = string.Empty;     // "WO-2026-04-008711"

    public WorkOrderType OrderType { get; set; } = WorkOrderType.Reactive;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Open;
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;

    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int? OutageId { get; set; }
    public Outage? Outage { get; set; }

    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public int? ServicePointId { get; set; }
    public ServicePoint? ServicePoint { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    public int? AssignedTechnicianId { get; set; }
    public Technician? AssignedTechnician { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DispatchedAt { get; set; }
    public DateTime? ArrivedOnSiteAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [Required, StringLength(200)]
    public string TitleEn { get; set; } = string.Empty;

    [StringLength(200)]
    public string? TitleAr { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(2000)]
    public string? ResolutionNotes { get; set; }

    /// <summary>True if the same WorkOrder required a follow-up visit. Surfaces second-visit-rate KPI.</summary>
    public bool RequiredSecondVisit { get; set; }
}

public enum WorkOrderType
{
    Reactive    = 1,   // triggered by a ticket / outage
    Preventive  = 2,   // scheduled maintenance
    Inspection  = 3,
    Installation = 4,
    Decommission = 5
}

public enum WorkOrderStatus
{
    Open        = 1,
    Assigned    = 2,
    InProgress  = 3,
    OnHold      = 4,
    Completed   = 5,
    Cancelled   = 6
}

public enum WorkOrderPriority
{
    Low      = 1,
    Normal   = 2,
    High     = 3,
    Critical = 4
}
