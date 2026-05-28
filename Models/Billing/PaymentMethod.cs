using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// How a payment was received. Lookup table (was a free-text string column on Bill).
/// Letting admins manage methods means the Copilot can answer "share of mobile-wallet
/// payments this quarter" without code changes when a new wallet provider is added.
/// </summary>
public class PaymentMethod
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string Code { get; set; } = string.Empty;            // "Cash", "BankTransfer", "Wallet", ...

    [Required, StringLength(80)]
    public string NameEn { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string NameAr { get; set; } = string.Empty;

    /// <summary>True for digital channels (wallets, online card) — excluded from cash-handling KPIs.</summary>
    public bool IsDigital { get; set; }

    /// <summary>Indicative percentage fee charged by the provider (0 for cash).</summary>
    public decimal FeePercent { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class PaymentMethodCodes
{
    public const string Cash         = "Cash";
    public const string BankTransfer = "BankTransfer";
    public const string Card         = "Card";
    public const string MobileWallet = "MobileWallet";    // MTN Cash / Syriatel Cash
    public const string OnlineWallet = "OnlineWallet";    // ePayment portals
}
