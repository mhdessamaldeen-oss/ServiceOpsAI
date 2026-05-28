using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Classification of customers for tariff, subsidy and SLA differentiation.
/// Residential / Commercial / Industrial / Government / Displaced are the
/// segments meaningful in the Syrian utility context — displaced-family
/// customers receive subsidized tariffs by regulation.
/// </summary>
public class CustomerSegment
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string NameAr { get; set; } = string.Empty;

    /// <summary>Whether this segment is generally eligible for subsidy programs
    /// (still gated by per-program rules). Drives default subsidy proposals.</summary>
    public bool IsSubsidyEligible { get; set; }

    /// <summary>Default priority floor on tickets from this segment — government
    /// and industrial customers typically have a higher floor than residential.</summary>
    public int DefaultPriorityFloor { get; set; } = 1;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class CustomerSegmentCodes
{
    public const string Residential = "Residential";
    public const string Commercial  = "Commercial";
    public const string Industrial  = "Industrial";
    public const string Government  = "Government";
    public const string Displaced   = "Displaced";
}
