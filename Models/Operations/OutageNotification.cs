using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A proactive notification sent to a customer about an outage / maintenance window.
/// Lets the Copilot answer "% of affected customers we notified before they called",
/// "average notification lead-time", and "channel mix per region". Created in fan-out
/// when an outage is logged or a maintenance schedule is published.
/// </summary>
public class OutageNotification
{
    public int Id { get; set; }

    public int? OutageId { get; set; }
    public Outage? Outage { get; set; }

    public int? MaintenanceScheduleId { get; set; }
    public MaintenanceSchedule? MaintenanceSchedule { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int? ServiceAccountId { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }

    public ContactChannel Channel { get; set; } = ContactChannel.Sms;

    [StringLength(30)]
    public string? SentToPhone { get; set; }

    [StringLength(200)]
    public string? SentToEmail { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Sent;

    [StringLength(1000)]
    public string? MessageEn { get; set; }

    [StringLength(1000)]
    public string? MessageAr { get; set; }
}

public enum NotificationStatus
{
    Pending   = 1,
    Sent      = 2,
    Delivered = 3,
    Read      = 4,
    Failed    = 5
}
