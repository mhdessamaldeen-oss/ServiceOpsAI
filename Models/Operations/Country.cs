using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

public class Country
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string NameAr { get; set; } = string.Empty;

    [Required, StringLength(3)]
    public string IsoCode { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
