using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Historical pricing per (ServiceType, Region). Lets the Copilot explain
/// bill changes ("why did Aleppo electricity jump in Sept 2025?") by
/// surfacing tariff changes, and compare regional pricing.
/// Bills implicitly use the active tariff for their period; an explicit
/// TariffId on Bill is not added to keep the model lean — the active
/// tariff is determined by Region + ServiceType + EffectiveFrom <= Bill.PeriodStart.
/// </summary>
public class Tariff
{
    public int Id { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public int? RegionId { get; set; }                  // null = applies country-wide
    public Region? Region { get; set; }

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }          // null = currently active

    public decimal BaseMonthlyFee { get; set; }          // SYP
    public decimal RatePerUnit { get; set; }             // SYP per unit (kWh/GB/m³/...)
    public decimal TaxPercent { get; set; }              // 0-100

    [StringLength(500)]
    public string? ChangeReasonEn { get; set; }
    [StringLength(500)]
    public string? ChangeReasonAr { get; set; }
}
