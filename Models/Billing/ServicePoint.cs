using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A physical service location — typically a building/unit where a meter is installed.
/// Distinct from the customer's registered address: a customer can own multiple
/// service points (home + shop), and a service point's address can outlive any
/// individual customer who was once contracted to it.
/// </summary>
public class ServicePoint
{
    public int Id { get; set; }

    /// <summary>Stable point code printed on the meter cabinet (e.g. "SP-DMSCN-0042-117").</summary>
    [Required, StringLength(40)]
    public string PointCode { get; set; } = string.Empty;

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    [StringLength(300)]
    public string? AddressLineEn { get; set; }

    [StringLength(300)]
    public string? AddressLineAr { get; set; }

    [StringLength(40)]
    public string? MeterNumber { get; set; }            // current physical meter id

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public ServicePointType PointType { get; set; } = ServicePointType.Residential;

    public bool IsActive { get; set; } = true;

    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}

public enum ServicePointType
{
    Residential = 1,
    Commercial  = 2,
    Industrial  = 3,
    Government  = 4,
    Mixed       = 5
}
