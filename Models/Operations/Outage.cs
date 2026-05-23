using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A service outage event — explicit (vs. discovered from ticket clusters).
/// Lets the Copilot answer outage-shape questions directly: frequency by district,
/// MTTR per department, planned vs unplanned breakdowns, "which outage caused the
/// most complaints", etc. Tickets can attach to an OutageId for attribution.
/// </summary>
public class Outage
{
    public int Id { get; set; }

    [StringLength(30)]
    public string OutageNumber { get; set; } = string.Empty;       // e.g. "OUT-2026-03-014"

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public OutageSeverity Severity { get; set; } = OutageSeverity.Minor;

    public OutageCause Cause { get; set; } = OutageCause.Unknown;

    public bool IsPlanned { get; set; }

    public int? AffectedCustomerCount { get; set; }

    [StringLength(500)]
    public string? TitleEn { get; set; }

    [StringLength(500)]
    public string? TitleAr { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }
}

public enum OutageSeverity { Minor = 1, Moderate = 2, Major = 3, Critical = 4 }

public enum OutageCause
{
    Unknown        = 0,
    Equipment      = 1,
    Weather        = 2,
    Maintenance    = 3,
    Construction   = 4,
    Overload       = 5,
    PowerCut       = 6,
    PipeBurst      = 7,
    FiberCut       = 8,
    Other          = 99
}
