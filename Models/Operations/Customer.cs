using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

public class Customer
{
    public int Id { get; set; }

    [Required, StringLength(150)]
    public string FullNameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string FullNameAr { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string NationalId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Email { get; set; }

    [Required, StringLength(30)]
    public string Phone { get; set; } = string.Empty;

    public int? RegionId { get; set; }
    public Region? Region { get; set; }

    [StringLength(300)]
    public string? AddressLineEn { get; set; }

    [StringLength(300)]
    public string? AddressLineAr { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    public DateTime SignupAt { get; set; } = DateTime.UtcNow;
    public DateTime? ChurnedAt { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public enum CustomerStatus
{
    Active = 1,
    Suspended = 2,
    Churned = 3
}
