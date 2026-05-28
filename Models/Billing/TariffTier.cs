using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// One block of a tiered tariff. Real utility pricing is rarely a single flat rate per
/// unit — it's "first 100 kWh at X, next 100 at Y, anything above at Z." Each
/// <see cref="Tariff"/> can have N tiers; the compiler/billing logic picks a tier by
/// the row's <see cref="FromUnit"/>/<see cref="ToUnit"/> bracket. Lets the Copilot
/// answer "how much did the third-tier rate change between 2024 and 2026" directly.
/// </summary>
public class TariffTier
{
    public int Id { get; set; }

    public int TariffId { get; set; }
    public Tariff? Tariff { get; set; }

    /// <summary>1-based block number — Tier 1 is the cheapest, Tier N the most expensive.</summary>
    public int TierNumber { get; set; }

    /// <summary>Inclusive lower bound (units of the service: kWh / m³ / GB / cyl).</summary>
    public decimal FromUnit { get; set; }

    /// <summary>Exclusive upper bound; null = unbounded (top tier).</summary>
    public decimal? ToUnit { get; set; }

    /// <summary>Price per unit within this block, in the tariff's base currency (SYP).</summary>
    public decimal RatePerUnit { get; set; }

    [StringLength(80)]
    public string? LabelEn { get; set; }                       // "Lifeline", "Standard", "Premium"

    [StringLength(80)]
    public string? LabelAr { get; set; }
}
