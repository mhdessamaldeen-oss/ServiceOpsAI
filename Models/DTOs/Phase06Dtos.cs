namespace ServiceOpsAI.Models.DTOs;

// Phase 06 — flat DTOs used by list pages (ProjectTo from EF) so we don't drag
// navigation objects across the wire / into Razor views.

public class CurrencyDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public bool IsBase { get; set; }
    public decimal ExchangeRateToBase { get; set; }
    public DateTime LastRateUpdate { get; set; }
    public bool IsActive { get; set; }
}

public class PaymentMethodDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public bool IsDigital { get; set; }
    public decimal FeePercent { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class CustomerSegmentDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public bool IsSubsidyEligible { get; set; }
    public int DefaultPriorityFloor { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class ServicePointDto
{
    public int Id { get; set; }
    public string PointCode { get; set; } = string.Empty;
    public string? RegionName { get; set; }
    public string? AddressLineEn { get; set; }
    public string? AddressLineAr { get; set; }
    public string? MeterNumber { get; set; }
    public string PointType { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime InstalledAt { get; set; }
}

public class ServiceAccountDto
{
    public int Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string? ServicePointCode { get; set; }
    public string? CustomerSegment { get; set; }
    public string? DepartmentName { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ActivatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public decimal? ContractedCapacity { get; set; }
    public string? CapacityUnit { get; set; }
}

public class PaymentDto
{
    public int Id { get; set; }
    public string PaymentReference { get; set; } = string.Empty;
    public int BillId { get; set; }
    public string? BillNumber { get; set; }
    public string? PaymentMethodName { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal AmountInBase { get; set; }
    public decimal ExchangeRateToBase { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? ExternalTransactionId { get; set; }
}

public class TariffTierDto
{
    public int Id { get; set; }
    public int TariffId { get; set; }
    public string? TariffServiceType { get; set; }
    public string? TariffRegion { get; set; }
    public int TierNumber { get; set; }
    public decimal FromUnit { get; set; }
    public decimal? ToUnit { get; set; }
    public decimal RatePerUnit { get; set; }
    public string? LabelEn { get; set; }
    public string? LabelAr { get; set; }
}

public class SubsidyDto
{
    public int Id { get; set; }
    public int? BillId { get; set; }
    public string? BillNumber { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? Segment { get; set; }
    public string ProgramCode { get; set; } = string.Empty;
    public string? ProgramNameEn { get; set; }
    public string? ProgramNameAr { get; set; }
    public decimal Amount { get; set; }
    public decimal? AppliedPercent { get; set; }
    public DateTime IssuedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class AssetDto
{
    public int Id { get; set; }
    public string AssetCode { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string? RegionName { get; set; }
    public string? DepartmentName { get; set; }
    public string AssetType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CommissionedAt { get; set; }
    public string? Specification { get; set; }
}

public class TechnicianDto
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullNameEn { get; set; } = string.Empty;
    public string FullNameAr { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? DepartmentName { get; set; }
    public string? PrimaryRegionName { get; set; }
    public string Specialty { get; set; } = string.Empty;
    public int YearsOfExperience { get; set; }
    public DateTime HiredAt { get; set; }
    public bool IsActive { get; set; }
}

public class WorkOrderDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public int? TicketId { get; set; }
    public string? TicketNumber { get; set; }
    public int? OutageId { get; set; }
    public string? OutageNumber { get; set; }
    public string? AssetName { get; set; }
    public string? TechnicianName { get; set; }
    public string? DepartmentName { get; set; }
    public string? RegionName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string TitleEn { get; set; } = string.Empty;
}

public class MaintenanceScheduleDto
{
    public int Id { get; set; }
    public string ScheduleNumber { get; set; } = string.Empty;
    public string? AssetName { get; set; }
    public string? RegionName { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public string Status { get; set; } = string.Empty;
    public string MaintenanceType { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public int? ExpectedAffectedCustomers { get; set; }
    public bool CustomersNotified { get; set; }
}

public class CallLogDto
{
    public int Id { get; set; }
    public string CallReference { get; set; } = string.Empty;
    public int? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int? RelatedTicketId { get; set; }
    public string? RelatedTicketNumber { get; set; }
}

public class OutageNotificationDto
{
    public int Id { get; set; }
    public int? OutageId { get; set; }
    public string? OutageNumber { get; set; }
    public int? MaintenanceScheduleId { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class SlaPolicyDto
{
    public int Id { get; set; }
    public string PolicyCode { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? CustomerSegment { get; set; }
    public string? ServiceType { get; set; }
    public string? Priority { get; set; }
    public int FirstResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
    public bool BusinessHoursOnly { get; set; }
    public bool IsActive { get; set; }
    public DateTime EffectiveFrom { get; set; }
}
