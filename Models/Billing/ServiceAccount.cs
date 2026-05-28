using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A contract between a Customer and the utility for a specific service at a specific
/// service point. Sits between Customer and Bill — a customer can have multiple
/// concurrent contracts (electricity + water at home, electricity at a shop) and a
/// service point can be transferred between customers when one moves out and another
/// moves in. Bills, payments and meter readings attach to the ServiceAccount.
/// </summary>
public class ServiceAccount
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    public string AccountNumber { get; set; } = string.Empty;   // "ACC-2024-DMSCN-00042"

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int ServicePointId { get; set; }
    public ServicePoint? ServicePoint { get; set; }

    public int? CustomerSegmentId { get; set; }
    public CustomerSegment? CustomerSegment { get; set; }

    /// <summary>Department responsible for this contract (regional ops team).</summary>
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeactivatedAt { get; set; }

    public ServiceAccountStatus Status { get; set; } = ServiceAccountStatus.Active;

    /// <summary>Optional contracted demand cap (kW for electricity, Mbps for internet).</summary>
    public decimal? ContractedCapacity { get; set; }

    [StringLength(10)]
    public string? CapacityUnit { get; set; }                   // "kW", "Mbps", ...

    [StringLength(500)]
    public string? Notes { get; set; }
}

public enum ServiceAccountStatus
{
    Active     = 1,
    Suspended  = 2,   // temp disconnect (non-payment, dispute)
    Terminated = 3,   // permanent
    Pending    = 4,   // contract drafted, not yet activated
}
