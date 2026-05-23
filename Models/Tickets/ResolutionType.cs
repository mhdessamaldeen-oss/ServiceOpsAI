using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Lookup table — how a ticket was closed (Resolved, NoFault, BillAdjusted,
/// Escalated, Cancelled, ...). Complements the free-text Ticket.ResolutionSummary
/// by giving the closure a queryable dimension the Copilot can group by.
/// </summary>
public class ResolutionType
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Code { get; set; } = string.Empty;          // "Resolved", "NoFault", ...

    [Required, StringLength(100)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string NameAr { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

public static class ResolutionTypeCodes
{
    public const string Resolved      = "Resolved";
    public const string NoFault       = "NoFault";
    public const string BillAdjusted  = "BillAdjusted";
    public const string Escalated     = "Escalated";
    public const string Cancelled     = "Cancelled";
    public const string OutageCleared = "OutageCleared";
}
