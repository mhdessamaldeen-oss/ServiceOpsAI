using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Lookup table — the categories of complaint a ticket can be (ServiceDown,
/// BillingDispute, MeterIssue, ...). Seeded with the original 7 values but
/// admins can add more from /ReferenceData.
/// </summary>
public class ComplaintType
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Code { get; set; } = string.Empty;          // "ServiceDown", "BillingDispute", ...

    [Required, StringLength(100)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string NameAr { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

public static class ComplaintTypeCodes
{
    public const string ServiceDown      = "ServiceDown";
    public const string ServiceDegraded  = "ServiceDegraded";
    public const string BillingDispute   = "BillingDispute";
    public const string MeterIssue       = "MeterIssue";
    public const string Disconnection    = "Disconnection";
    public const string NewConnection    = "NewConnection";
    public const string Other            = "Other";
}
