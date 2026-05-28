using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A payment event. Distinct from Bill — a bill can be paid in multiple installments,
/// a single payment can apply to multiple bills (rare; modelled later if needed), and
/// payments arrive in different currencies. Capturing payments separately lets the
/// Copilot answer "total collected by channel" / "average days-to-pay" / "outstanding
/// balance after partial payments" — none of which the boolean PaidAt on Bill supports.
/// </summary>
public class Payment
{
    public int Id { get; set; }

    [Required, StringLength(40)]
    public string PaymentReference { get; set; } = string.Empty;   // "PAY-2026-04-0001234"

    public int BillId { get; set; }
    public Bill? Bill { get; set; }

    public int? ServiceAccountId { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }

    public int? PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }

    /// <summary>Amount in the payment's own currency.</summary>
    public decimal Amount { get; set; }

    /// <summary>Rate captured at payment time so historic conversions don't drift.</summary>
    public decimal ExchangeRateToBase { get; set; } = 1m;

    /// <summary>Amount converted to the platform's base currency (SYP) using
    /// <see cref="ExchangeRateToBase"/>. Stored so reports don't need to re-multiply
    /// (and so a later rate change can't silently shift historical totals).</summary>
    public decimal AmountInBase { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Posted;

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optional channel transaction id for reconciliation (bank ref, wallet txid).</summary>
    [StringLength(120)]
    public string? ExternalTransactionId { get; set; }

    [StringLength(450)]
    public string? ReceivedByUserId { get; set; }
    public ApplicationUser? ReceivedByUser { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

public enum PaymentStatus
{
    Pending  = 1,   // initiated, awaiting confirmation (wallet, online)
    Posted   = 2,   // applied to the bill
    Refunded = 3,
    Reversed = 4    // chargeback / clerical reversal
}
