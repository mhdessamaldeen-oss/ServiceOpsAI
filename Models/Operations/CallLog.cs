using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// One contact event — phone call, SMS thread, WhatsApp conversation. Distinct from
/// Ticket: most calls don't escalate to a ticket. Captures repeat-caller patterns
/// ("how many customers called >3 times in the past 7 days about the same outage"),
/// average handle time per channel, and the precursor signal before formal complaints.
/// </summary>
public class CallLog
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    public string CallReference { get; set; } = string.Empty;   // "CL-2026-04-1138772"

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [StringLength(30)]
    public string? CallerPhone { get; set; }                    // when the call wasn't matched to a Customer

    public ContactDirection Direction { get; set; } = ContactDirection.Inbound;
    public ContactChannel Channel { get; set; } = ContactChannel.Phone;

    /// <summary>Agent who handled the contact.</summary>
    [StringLength(450)]
    public string? HandledByUserId { get; set; }
    public ApplicationUser? HandledByUser { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public int? DurationSeconds { get; set; }

    public ContactOutcome Outcome { get; set; } = ContactOutcome.Resolved;

    /// <summary>Optional link to the ticket this call escalated to.</summary>
    public int? RelatedTicketId { get; set; }
    public Ticket? RelatedTicket { get; set; }

    public int? RelatedOutageId { get; set; }
    public Outage? RelatedOutage { get; set; }

    [StringLength(2000)]
    public string? Summary { get; set; }
}

public enum ContactDirection
{
    Inbound  = 1,
    Outbound = 2
}

public enum ContactChannel
{
    Phone      = 1,
    Sms        = 2,
    Whatsapp   = 3,
    Email      = 4,
    InAppChat  = 5,
    Walkin     = 6
}

public enum ContactOutcome
{
    Resolved          = 1,
    EscalatedToTicket = 2,
    CallbackScheduled = 3,
    Abandoned         = 4,
    Misdial           = 5
}
