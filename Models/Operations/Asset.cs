using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A piece of utility infrastructure — substation, transformer, water pump, fiber
/// node, gas regulator. Outages and work orders attach to a specific asset, which
/// lets the Copilot answer "which transformer caused the most outages in Aleppo
/// last year" and "average MTTR per asset type." Failure data per asset is the
/// foundation of any predictive-maintenance work later.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string AssetCode { get; set; } = string.Empty;       // "TRF-DMSCN-MZH-007"

    [Required, StringLength(150)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string NameAr { get; set; } = string.Empty;

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public AssetType AssetType { get; set; } = AssetType.Other;

    public AssetStatus Status { get; set; } = AssetStatus.Operational;

    public DateTime CommissionedAt { get; set; }

    public DateTime? DecommissionedAt { get; set; }

    /// <summary>Manufacturer / capacity / voltage class — short free-text spec line.</summary>
    [StringLength(300)]
    public string? Specification { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>Optional parent (e.g. a transformer parented to a substation).</summary>
    public int? ParentAssetId { get; set; }
    public Asset? ParentAsset { get; set; }
}

public enum AssetType
{
    Substation       = 1,
    Transformer      = 2,
    PowerLine        = 3,
    PumpingStation   = 4,
    WaterPipeline    = 5,
    GasRegulator     = 6,
    FiberNode        = 7,
    DslamCabinet     = 8,
    Generator        = 9,
    Other            = 99
}

public enum AssetStatus
{
    Operational     = 1,
    UnderMaintenance = 2,
    Faulty          = 3,
    Decommissioned  = 4
}
