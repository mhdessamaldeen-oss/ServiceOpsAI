using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models;

/// <summary>
/// A periodic meter reading per (Customer, ServiceType). Bills derive
/// UsageQuantity from the difference between consecutive readings.
/// Anomaly detection ("usage jumped 3x without explanation") is the
/// signature AI use case for this table.
/// </summary>
public class MeterReading
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }

    public DateTime ReadingDate { get; set; }

    public decimal Value { get; set; }                  // cumulative meter reading

    public decimal? Consumption { get; set; }           // computed delta from previous reading

    public MeterReadingType ReaderType { get; set; } = MeterReadingType.Actual;

    [StringLength(40)]
    public string? MeterNumber { get; set; }            // physical meter id (string for prefix patterns)

    [StringLength(500)]
    public string? Notes { get; set; }
}

public enum MeterReadingType
{
    Actual    = 1,    // physical visit / smart meter
    Estimated = 2,    // based on historical average
    Customer  = 3,    // customer self-reported
    Audit     = 4     // technician verification
}
