using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models.Operations;

public class Department
{
    public int Id { get; set; }

    [Required, StringLength(150)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string NameAr { get; set; } = string.Empty;

    public ServiceType ServiceType { get; set; }

    public int? RegionId { get; set; }

    [StringLength(450)]
    public string? ManagerUserId { get; set; }

    [StringLength(30)]
    public string? ContactPhone { get; set; }

    [StringLength(200)]
    public string? ContactEmail { get; set; }

    public bool IsActive { get; set; } = true;
}
