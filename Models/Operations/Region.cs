using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

public class Region
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string NameAr { get; set; } = string.Empty;

    public int CountryId { get; set; }
    public Country? Country { get; set; }

    public int? ParentRegionId { get; set; }
    public Region? ParentRegion { get; set; }
    public ICollection<Region> ChildRegions { get; set; } = new List<Region>();

    public RegionType RegionType { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum RegionType
{
    Governorate = 1,
    District = 2
}
