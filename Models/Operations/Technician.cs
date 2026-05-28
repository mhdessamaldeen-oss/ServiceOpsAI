using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Field staff — distinct from <see cref="ApplicationUser"/> (which represents agents
/// and managers who use the web app). A Technician is the person who physically
/// goes to a site to execute a WorkOrder. Linking to an ApplicationUser is optional —
/// most technicians don't log in. Tracked here so the Copilot can compute
/// "average MTTR by technician", "second-visit rate by team", and "which technicians
/// closed the most outages this quarter".
/// </summary>
public class Technician
{
    public int Id { get; set; }

    [Required, StringLength(20)]
    public string EmployeeCode { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string FullNameEn { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string FullNameAr { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Phone { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? PrimaryRegionId { get; set; }
    public Region? PrimaryRegion { get; set; }

    public TechnicianSpecialty Specialty { get; set; } = TechnicianSpecialty.General;

    public int YearsOfExperience { get; set; }

    /// <summary>Optional link to a platform login if this technician also uses the app.</summary>
    [StringLength(450)]
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public DateTime HiredAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum TechnicianSpecialty
{
    General      = 0,
    Electrical   = 1,
    Plumbing     = 2,
    Gas          = 3,
    Telecom      = 4,
    Civil        = 5,
    SmartMeter   = 6
}
