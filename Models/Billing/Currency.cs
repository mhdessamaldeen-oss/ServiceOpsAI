using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Currency reference. The Syrian utility platform receives payments in SYP, USD, EUR
/// and TRY (border-region customers). Bills are issued in a base currency; payments
/// can arrive in any currency and get converted using <see cref="ExchangeRateToBase"/>
/// captured at the time of the payment (the rate is stored on the Payment itself —
/// this table just provides the canonical list and the *current* mid-market reference
/// rate used as a default when a new payment is logged).
/// </summary>
public class Currency
{
    public int Id { get; set; }

    /// <summary>ISO-4217 code: SYP, USD, EUR, TRY. Used by code as a stable identifier.</summary>
    [Required, StringLength(3)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(60)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(60)]
    public string NameAr { get; set; } = string.Empty;

    [Required, StringLength(5)]
    public string Symbol { get; set; } = string.Empty;          // "ل.س", "$", "€", "₺"

    /// <summary>True for exactly one row — the platform's reporting base currency (SYP).</summary>
    public bool IsBase { get; set; }

    /// <summary>Indicative current rate of 1 unit of this currency in base units (SYP).
    /// For the base currency itself this is 1. Per-payment rates are captured on the
    /// Payment row so historic conversions don't drift when this is updated.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    public DateTime LastRateUpdate { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}

public static class CurrencyCodes
{
    public const string SYP = "SYP";
    public const string USD = "USD";
    public const string EUR = "EUR";
    public const string TRY = "TRY";
}
