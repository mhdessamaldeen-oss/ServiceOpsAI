using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

public class Bill
{
    public int Id { get; set; }

    [StringLength(30)]
    public string BillNumber { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public ServiceType ServiceType { get; set; }

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public decimal BaseAmount { get; set; }
    public decimal UsageAmount { get; set; }
    public decimal Taxes { get; set; }
    public decimal TotalAmount { get; set; }

    public decimal? UsageQuantity { get; set; }

    [StringLength(10)]
    public string? UsageUnit { get; set; }

    public BillStatus Status { get; set; } = BillStatus.Issued;

    public DateTime IssuedAt { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidAt { get; set; }

    [StringLength(50)]
    public string? PaymentMethod { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

public enum BillStatus
{
    Issued = 1,
    Paid = 2,
    Overdue = 3,
    Disputed = 4,
    Cancelled = 5
}
