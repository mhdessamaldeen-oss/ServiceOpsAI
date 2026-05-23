using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ServiceOpsAI.Models;

public class Department
{
    public int Id { get; set; }

    [Required, StringLength(150)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string NameAr { get; set; } = string.Empty;

    // Backwards-compat alias for the original `Entity.Name` field.
    // Existing callers (DbSeeder, controllers, views) continue to read/write `.Name`;
    // new code should use NameEn/NameAr directly.
    [NotMapped]
    public string Name
    {
        get => NameEn;
        set => NameEn = value;
    }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    [StringLength(450)]
    public string? ManagerUserId { get; set; }

    [StringLength(30)]
    public string? ContactPhone { get; set; }

    [StringLength(200)]
    public string? ContactEmail { get; set; }

    public bool IsActive { get; set; } = true;
}
