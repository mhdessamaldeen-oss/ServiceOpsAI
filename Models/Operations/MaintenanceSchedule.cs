using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A planned maintenance window for an asset (or a region). Lets the Copilot answer
/// "% of outages that happened inside a planned-maintenance window" and "next
/// scheduled maintenance per substation". When an Outage falls inside a window, the
/// IsPlanned flag should be true and the AffectedCustomerCount becomes the *forewarned*
/// count.
/// </summary>
public class MaintenanceSchedule
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    public string ScheduleNumber { get; set; } = string.Empty;  // "MS-2026-04-0017"

    public int? AssetId { get; set; }
    public Asset? Asset { get; set; }

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }

    public DateTime? ActualStart { get; set; }
    public DateTime? ActualEnd { get; set; }

    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.Scheduled;

    public MaintenanceType MaintenanceType { get; set; } = MaintenanceType.Preventive;

    [Required, StringLength(200)]
    public string TitleEn { get; set; } = string.Empty;

    [StringLength(200)]
    public string? TitleAr { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Expected number of customers affected (informs the customer notifications fan-out).</summary>
    public int? ExpectedAffectedCustomers { get; set; }

    public bool CustomersNotified { get; set; }
}

public enum MaintenanceStatus
{
    Scheduled = 1,
    InProgress = 2,
    Completed  = 3,
    Cancelled  = 4,
    Deferred   = 5
}

public enum MaintenanceType
{
    Preventive  = 1,
    Corrective  = 2,
    Inspection  = 3,
    Upgrade     = 4
}
