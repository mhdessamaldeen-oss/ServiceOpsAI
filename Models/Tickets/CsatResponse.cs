using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// Customer satisfaction survey response, attached to a resolved Ticket.
/// Provides labeled outcomes — the rarest, most valuable ingredient for AI
/// quality measurement. Score 1-5 + free-text comment + optional sentiment.
/// </summary>
public class CsatResponse
{
    public int Id { get; set; }

    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    [Range(1, 5)]
    public int Score { get; set; }                   // 1=very dissatisfied, 5=very satisfied

    [StringLength(1000)]
    public string? CommentEn { get; set; }

    [StringLength(1000)]
    public string? CommentAr { get; set; }

    public CsatSentiment? Sentiment { get; set; }    // null = not yet analyzed by Copilot

    public DateTime RespondedAt { get; set; } = DateTime.UtcNow;

    [StringLength(40)]
    public string? ResponseChannel { get; set; }     // "SMS", "Email", "InApp", "Phone"
}

public enum CsatSentiment
{
    Negative = 1,
    Neutral  = 2,
    Positive = 3
}
