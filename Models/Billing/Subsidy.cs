using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A government / regulator credit applied to a customer's bill. Syria-relevant:
/// displaced-family programs, low-income households, government employee discounts.
/// One row per (program × customer × bill) so the Copilot can answer "total subsidy
/// disbursed by program this quarter" and "average bill after subsidy by segment".
/// </summary>
public class Subsidy
{
    public int Id { get; set; }

    public int? BillId { get; set; }
    public Bill? Bill { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? CustomerSegmentId { get; set; }
    public CustomerSegment? CustomerSegment { get; set; }

    [Required, StringLength(80)]
    public string ProgramCode { get; set; } = string.Empty;     // "DISPLACED-2026", "LOW-INCOME-RES"

    [StringLength(150)]
    public string? ProgramNameEn { get; set; }

    [StringLength(150)]
    public string? ProgramNameAr { get; set; }

    /// <summary>Subsidy amount in base currency (SYP).</summary>
    public decimal Amount { get; set; }

    /// <summary>For percentage-style subsidies — informational; <see cref="Amount"/> is authoritative.</summary>
    public decimal? AppliedPercent { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public SubsidyStatus Status { get; set; } = SubsidyStatus.Applied;

    [StringLength(450)]
    public string? AuthorizedByUserId { get; set; }
    public ApplicationUser? AuthorizedByUser { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

public enum SubsidyStatus
{
    Pending   = 1,
    Applied   = 2,
    Revoked   = 3,    // discovered ineligible after the fact
}
