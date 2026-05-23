using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Lookup table — the kinds of utility services the platform supports.
/// Seeded with Electricity / Internet / Water / Gas; admins can add new ones
/// (e.g. "Government process") via /ReferenceData without a code change.
///
/// The <see cref="Code"/> column is a stable machine-friendly identifier that
/// seed code, the storytelling seeder, and the Copilot use to reason about a
/// specific service type (e.g. SYP ranges differ per Code). The Id is the FK
/// other tables (Bill, Department) point at.
/// </summary>
public class ServiceType
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Code { get; set; } = string.Empty;          // "Electricity", "Internet", ...

    [Required, StringLength(100)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string NameAr { get; set; } = string.Empty;

    [StringLength(20)]
    public string? Unit { get; set; }                          // "kWh", "GB", "m³", "cyl", ...

    [StringLength(50)]
    public string? IconClass { get; set; }                     // optional Bootstrap-icon class

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Stable identifier codes for the seeded service types. Code that needs to
/// switch on a specific service (e.g. bill amount ranges in the seeder) uses
/// these constants by comparing <c>ServiceType.Code == ServiceTypeCodes.Electricity</c>
/// — adding a new <see cref="ServiceType"/> row in the DB doesn't require touching
/// these constants, and code that doesn't recognize a new Code falls through to
/// its default branch.
/// </summary>
public static class ServiceTypeCodes
{
    public const string Electricity = "Electricity";
    public const string Internet    = "Internet";
    public const string Water       = "Water";
    public const string Gas         = "Gas";
}
