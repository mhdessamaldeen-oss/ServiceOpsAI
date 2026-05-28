using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Department> Departments { get; set; }
    public DbSet<Country> Countries { get; set; }
    public DbSet<Region> Regions { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<Bill> Bills { get; set; }
    public DbSet<ServiceType> ServiceTypes { get; set; }
    public DbSet<ComplaintType> ComplaintTypes { get; set; }
    public DbSet<ResolutionType> ResolutionTypes { get; set; }
    public DbSet<Outage> Outages { get; set; }
    public DbSet<MeterReading> MeterReadings { get; set; }
    public DbSet<CsatResponse> CsatResponses { get; set; }
    public DbSet<Tariff> Tariffs { get; set; }
    public DbSet<TicketCategory> TicketCategories { get; set; }
    public DbSet<TicketPriority> TicketPriorities { get; set; }
    public DbSet<TicketStatus> TicketStatuses { get; set; }
    public DbSet<TicketSource> TicketSources { get; set; }
    public DbSet<Ticket> Tickets { get; set; }
    public DbSet<TicketComment> TicketComments { get; set; }
    public DbSet<TicketAttachment> TicketAttachments { get; set; }
    public DbSet<TicketHistory> TicketHistories { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<CustomTheme> CustomThemes { get; set; }
    public DbSet<ExternalApiSetting> ExternalApiSettings { get; set; }
    public DbSet<TicketAiAnalysis> TicketAiAnalyses { get; set; }
    public DbSet<TicketAiAnalysisLog> TicketAiAnalysisLogs { get; set; }
    public DbSet<TicketSemanticEmbedding> TicketSemanticEmbeddings { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.RetrievalBenchmarkRun> RetrievalBenchmarkRuns { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.CopilotToolDefinition> CopilotToolDefinitions { get; set; }

    public DbSet<ServiceOpsAI.Models.AI.CopilotTraceHistory> CopilotTraceHistories { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.CopilotAssessmentRunSummary> CopilotAssessmentRunSummaries { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.CopilotChatSession> CopilotChatSessions { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.CopilotChatMessageEntity> CopilotChatMessages { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.GeminiApiKey> GeminiApiKeys { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.GroqApiKey> GroqApiKeys { get; set; }
    public DbSet<ServiceOpsAI.Models.AI.ModelPricing> ModelPricings { get; set; }

    // Phase 06 — Billing/Contract depth
    public DbSet<Currency> Currencies { get; set; }
    public DbSet<PaymentMethod> PaymentMethods { get; set; }
    public DbSet<CustomerSegment> CustomerSegments { get; set; }
    public DbSet<ServicePoint> ServicePoints { get; set; }
    public DbSet<ServiceAccount> ServiceAccounts { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<TariffTier> TariffTiers { get; set; }
    public DbSet<Subsidy> Subsidies { get; set; }

    // Phase 06 — Field Operations
    public DbSet<Asset> Assets { get; set; }
    public DbSet<Technician> Technicians { get; set; }
    public DbSet<WorkOrder> WorkOrders { get; set; }
    public DbSet<MaintenanceSchedule> MaintenanceSchedules { get; set; }

    // Phase 06 — Customer Voice
    public DbSet<CallLog> CallLogs { get; set; }
    public DbSet<OutageNotification> OutageNotifications { get; set; }
    public DbSet<SlaPolicy> SlaPolicies { get; set; }


    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Lookup tables — Code is the stable machine identifier, must be unique.
        builder.Entity<ServiceType>().HasIndex(s => s.Code).IsUnique();
        builder.Entity<ComplaintType>().HasIndex(c => c.Code).IsUnique();
        builder.Entity<ResolutionType>().HasIndex(r => r.Code).IsUnique();

        // TicketCategory hierarchy + Tier + optional link to ServiceType.
        builder.Entity<TicketCategory>().Property(c => c.Tier).HasConversion<string>();
        builder.Entity<TicketCategory>()
            .HasOne(c => c.ParentCategory)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<TicketCategory>()
            .HasOne(c => c.ServiceType)
            .WithMany()
            .HasForeignKey(c => c.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<TicketCategory>().HasIndex(c => c.ParentCategoryId);
        builder.Entity<TicketCategory>().HasIndex(c => c.ServiceTypeId);

        // Outage (Phase F) — explicit service outage events.
        builder.Entity<Outage>().Property(o => o.Severity).HasConversion<string>();
        builder.Entity<Outage>().Property(o => o.Cause).HasConversion<string>();
        builder.Entity<Outage>().HasIndex(o => o.OutageNumber).IsUnique();
        builder.Entity<Outage>().HasIndex(o => new { o.ServiceTypeId, o.StartedAt });
        builder.Entity<Outage>().HasIndex(o => o.RegionId);
        builder.Entity<Outage>()
            .HasOne(o => o.Region)
            .WithMany()
            .HasForeignKey(o => o.RegionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Outage>()
            .HasOne(o => o.ServiceType)
            .WithMany()
            .HasForeignKey(o => o.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Outage>()
            .HasOne(o => o.Department)
            .WithMany()
            .HasForeignKey(o => o.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket.OutageId — optional attribution to a specific Outage.
        builder.Entity<Ticket>()
            .HasOne(t => t.Outage)
            .WithMany()
            .HasForeignKey(t => t.OutageId)
            .OnDelete(DeleteBehavior.SetNull);

        // MeterReading (Phase G) — periodic consumption readings.
        builder.Entity<MeterReading>().Property(m => m.ReaderType).HasConversion<string>();
        builder.Entity<MeterReading>().Property(m => m.Value).HasPrecision(14, 2);
        builder.Entity<MeterReading>().Property(m => m.Consumption).HasPrecision(14, 2);
        builder.Entity<MeterReading>()
            .HasIndex(m => new { m.CustomerId, m.ServiceTypeId, m.ReadingDate });
        builder.Entity<MeterReading>()
            .HasOne(m => m.Customer).WithMany().HasForeignKey(m => m.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<MeterReading>()
            .HasOne(m => m.ServiceType).WithMany().HasForeignKey(m => m.ServiceTypeId).OnDelete(DeleteBehavior.Restrict);

        // CSAT (Phase H) — labeled outcomes for Copilot quality evaluation.
        builder.Entity<CsatResponse>().Property(c => c.Sentiment).HasConversion<string>();
        builder.Entity<CsatResponse>()
            .HasIndex(c => c.TicketId).IsUnique();           // one CSAT per ticket max
        builder.Entity<CsatResponse>()
            .HasOne(c => c.Ticket).WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Restrict);

        // Tariff (Phase I) — historical pricing per service/region.
        builder.Entity<Tariff>().Property(t => t.BaseMonthlyFee).HasPrecision(12, 2);
        builder.Entity<Tariff>().Property(t => t.RatePerUnit).HasPrecision(12, 4);
        builder.Entity<Tariff>().Property(t => t.TaxPercent).HasPrecision(5, 2);
        builder.Entity<Tariff>()
            .HasIndex(t => new { t.ServiceTypeId, t.RegionId, t.EffectiveFrom });
        builder.Entity<Tariff>()
            .HasOne(t => t.ServiceType).WithMany().HasForeignKey(t => t.ServiceTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Tariff>()
            .HasOne(t => t.Region).WithMany().HasForeignKey(t => t.RegionId).OnDelete(DeleteBehavior.Restrict);

        // Department.ServiceType: was enum string, now FK to ServiceTypes.
        builder.Entity<Department>()
            .HasOne(d => d.ServiceType)
            .WithMany()
            .HasForeignKey(d => d.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Region>().Property(r => r.RegionType).HasConversion<string>();
        builder.Entity<Region>()
            .HasOne(r => r.ParentRegion)
            .WithMany(r => r.ChildRegions)
            .HasForeignKey(r => r.ParentRegionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Region>()
            .HasIndex(r => new { r.CountryId, r.RegionType });

        builder.Entity<Country>()
            .HasIndex(c => c.IsoCode)
            .IsUnique();

        builder.Entity<Customer>().Property(c => c.Status).HasConversion<string>();
        builder.Entity<Customer>()
            .HasIndex(c => c.NationalId)
            .IsUnique();

        builder.Entity<Bill>().Property(b => b.Status).HasConversion<string>();
        // Bill.ServiceType: FK to ServiceTypes.
        builder.Entity<Bill>()
            .HasOne(b => b.ServiceType)
            .WithMany()
            .HasForeignKey(b => b.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Bill>().Property(b => b.BaseAmount).HasPrecision(12, 2);
        builder.Entity<Bill>().Property(b => b.UsageAmount).HasPrecision(12, 2);
        builder.Entity<Bill>().Property(b => b.Taxes).HasPrecision(12, 2);
        builder.Entity<Bill>().Property(b => b.TotalAmount).HasPrecision(12, 2);
        builder.Entity<Bill>().Property(b => b.UsageQuantity).HasPrecision(12, 2);
        builder.Entity<Bill>()
            .HasIndex(b => new { b.CustomerId, b.PeriodStart });
        builder.Entity<Bill>()
            .HasIndex(b => new { b.DepartmentId, b.Status });
        builder.Entity<Bill>()
            .HasIndex(b => b.Status);
        builder.Entity<Bill>()
            .HasIndex(b => b.BillNumber)
            .IsUnique();

        // Ticket.ComplaintType: FK to ComplaintTypes (was enum).
        builder.Entity<Ticket>()
            .HasOne(t => t.ComplaintType)
            .WithMany()
            .HasForeignKey(t => t.ComplaintTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        // Ticket.ResolutionType: FK to ResolutionTypes (new).
        builder.Entity<Ticket>()
            .HasOne(t => t.ResolutionType)
            .WithMany()
            .HasForeignKey(t => t.ResolutionTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        // Ticket.Region: FK to Regions — issue location, distinct from customer's home.
        builder.Entity<Ticket>()
            .HasOne(t => t.Region)
            .WithMany()
            .HasForeignKey(t => t.RegionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Ticket>()
            .HasOne(t => t.Customer)
            .WithMany()
            .HasForeignKey(t => t.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Ticket>()
            .HasOne(t => t.RelatedBill)
            .WithMany()
            .HasForeignKey(t => t.RelatedBillId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Entity<Ticket>()
            .HasIndex(t => new { t.CustomerId, t.CreatedAt });
        builder.Entity<Ticket>()
            .HasIndex(t => new { t.DepartmentId, t.StatusId });

        builder.Entity<Ticket>()
            .HasOne(t => t.AssignedToUser)
            .WithMany()
            .HasForeignKey(t => t.AssignedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Ticket>()
            .HasOne(t => t.EscalatedToUser)
            .WithMany()
            .HasForeignKey(t => t.EscalatedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Ticket>()
            .HasOne(t => t.ResolvedByUser)
            .WithMany()
            .HasForeignKey(t => t.ResolvedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Ticket>()
            .HasOne(t => t.ResolutionApprovedByUser)
            .WithMany()
            .HasForeignKey(t => t.ResolutionApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // ─────────────────────────────────────────────────────────────────────
        // Phase 06 — depth tables (Billing / Field Ops / Customer Voice).
        // Conventions:
        //   • Enums stored as strings (HasConversion<string>) — keeps the DB readable
        //     and the Copilot's WHERE-clause matching deterministic across migrations.
        //   • Decimals get explicit precision so reporting math is exact.
        //   • Stable identifier columns (Code, Number, Reference) get a unique index.
        //   • All FKs default to Restrict via the global loop below.
        // ─────────────────────────────────────────────────────────────────────

        // Currency
        builder.Entity<Currency>().HasIndex(c => c.Code).IsUnique();
        builder.Entity<Currency>().Property(c => c.ExchangeRateToBase).HasPrecision(18, 6);

        // PaymentMethod
        builder.Entity<PaymentMethod>().HasIndex(p => p.Code).IsUnique();
        builder.Entity<PaymentMethod>().Property(p => p.FeePercent).HasPrecision(5, 2);

        // CustomerSegment
        builder.Entity<CustomerSegment>().HasIndex(s => s.Code).IsUnique();

        // ServicePoint
        builder.Entity<ServicePoint>().Property(p => p.PointType).HasConversion<string>();
        builder.Entity<ServicePoint>().Property(p => p.Latitude).HasPrecision(10, 6);
        builder.Entity<ServicePoint>().Property(p => p.Longitude).HasPrecision(10, 6);
        builder.Entity<ServicePoint>().HasIndex(p => p.PointCode).IsUnique();
        builder.Entity<ServicePoint>().HasIndex(p => p.RegionId);
        builder.Entity<ServicePoint>()
            .HasOne(p => p.Region).WithMany().HasForeignKey(p => p.RegionId).OnDelete(DeleteBehavior.Restrict);

        // ServiceAccount
        builder.Entity<ServiceAccount>().Property(a => a.Status).HasConversion<string>();
        builder.Entity<ServiceAccount>().Property(a => a.ContractedCapacity).HasPrecision(12, 2);
        builder.Entity<ServiceAccount>().HasIndex(a => a.AccountNumber).IsUnique();
        builder.Entity<ServiceAccount>().HasIndex(a => new { a.CustomerId, a.Status });
        builder.Entity<ServiceAccount>().HasIndex(a => new { a.ServiceTypeId, a.Status });
        builder.Entity<ServiceAccount>()
            .HasOne(a => a.Customer).WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ServiceAccount>()
            .HasOne(a => a.ServiceType).WithMany().HasForeignKey(a => a.ServiceTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ServiceAccount>()
            .HasOne(a => a.ServicePoint).WithMany().HasForeignKey(a => a.ServicePointId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ServiceAccount>()
            .HasOne(a => a.CustomerSegment).WithMany().HasForeignKey(a => a.CustomerSegmentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ServiceAccount>()
            .HasOne(a => a.Department).WithMany().HasForeignKey(a => a.DepartmentId).OnDelete(DeleteBehavior.Restrict);

        // Payment
        builder.Entity<Payment>().Property(p => p.Status).HasConversion<string>();
        builder.Entity<Payment>().Property(p => p.Amount).HasPrecision(14, 2);
        builder.Entity<Payment>().Property(p => p.AmountInBase).HasPrecision(14, 2);
        builder.Entity<Payment>().Property(p => p.ExchangeRateToBase).HasPrecision(18, 6);
        builder.Entity<Payment>().HasIndex(p => p.PaymentReference).IsUnique();
        builder.Entity<Payment>().HasIndex(p => new { p.BillId, p.Status });
        builder.Entity<Payment>().HasIndex(p => p.PaidAt);
        builder.Entity<Payment>()
            .HasOne(p => p.Bill).WithMany().HasForeignKey(p => p.BillId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Payment>()
            .HasOne(p => p.ServiceAccount).WithMany().HasForeignKey(p => p.ServiceAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Payment>()
            .HasOne(p => p.PaymentMethod).WithMany().HasForeignKey(p => p.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Payment>()
            .HasOne(p => p.Currency).WithMany().HasForeignKey(p => p.CurrencyId).OnDelete(DeleteBehavior.Restrict);

        // TariffTier
        builder.Entity<TariffTier>().Property(t => t.FromUnit).HasPrecision(14, 2);
        builder.Entity<TariffTier>().Property(t => t.ToUnit).HasPrecision(14, 2);
        builder.Entity<TariffTier>().Property(t => t.RatePerUnit).HasPrecision(12, 4);
        builder.Entity<TariffTier>().HasIndex(t => new { t.TariffId, t.TierNumber }).IsUnique();
        builder.Entity<TariffTier>()
            .HasOne(t => t.Tariff).WithMany().HasForeignKey(t => t.TariffId).OnDelete(DeleteBehavior.Restrict);

        // Subsidy
        builder.Entity<Subsidy>().Property(s => s.Status).HasConversion<string>();
        builder.Entity<Subsidy>().Property(s => s.Amount).HasPrecision(14, 2);
        builder.Entity<Subsidy>().Property(s => s.AppliedPercent).HasPrecision(5, 2);
        builder.Entity<Subsidy>().HasIndex(s => new { s.CustomerId, s.IssuedAt });
        builder.Entity<Subsidy>().HasIndex(s => s.ProgramCode);
        builder.Entity<Subsidy>()
            .HasOne(s => s.Bill).WithMany().HasForeignKey(s => s.BillId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Subsidy>()
            .HasOne(s => s.Customer).WithMany().HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Subsidy>()
            .HasOne(s => s.CustomerSegment).WithMany().HasForeignKey(s => s.CustomerSegmentId).OnDelete(DeleteBehavior.Restrict);

        // Asset
        builder.Entity<Asset>().Property(a => a.AssetType).HasConversion<string>();
        builder.Entity<Asset>().Property(a => a.Status).HasConversion<string>();
        builder.Entity<Asset>().Property(a => a.Latitude).HasPrecision(10, 6);
        builder.Entity<Asset>().Property(a => a.Longitude).HasPrecision(10, 6);
        builder.Entity<Asset>().HasIndex(a => a.AssetCode).IsUnique();
        builder.Entity<Asset>().HasIndex(a => new { a.ServiceTypeId, a.AssetType });
        builder.Entity<Asset>().HasIndex(a => a.RegionId);
        builder.Entity<Asset>()
            .HasOne(a => a.ServiceType).WithMany().HasForeignKey(a => a.ServiceTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Asset>()
            .HasOne(a => a.Region).WithMany().HasForeignKey(a => a.RegionId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Asset>()
            .HasOne(a => a.Department).WithMany().HasForeignKey(a => a.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Asset>()
            .HasOne(a => a.ParentAsset).WithMany().HasForeignKey(a => a.ParentAssetId).OnDelete(DeleteBehavior.Restrict);

        // Technician
        builder.Entity<Technician>().Property(t => t.Specialty).HasConversion<string>();
        builder.Entity<Technician>().HasIndex(t => t.EmployeeCode).IsUnique();
        builder.Entity<Technician>().HasIndex(t => new { t.DepartmentId, t.IsActive });
        builder.Entity<Technician>()
            .HasOne(t => t.Department).WithMany().HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Technician>()
            .HasOne(t => t.PrimaryRegion).WithMany().HasForeignKey(t => t.PrimaryRegionId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Technician>()
            .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);

        // WorkOrder
        builder.Entity<WorkOrder>().Property(w => w.OrderType).HasConversion<string>();
        builder.Entity<WorkOrder>().Property(w => w.Status).HasConversion<string>();
        builder.Entity<WorkOrder>().Property(w => w.Priority).HasConversion<string>();
        builder.Entity<WorkOrder>().HasIndex(w => w.OrderNumber).IsUnique();
        builder.Entity<WorkOrder>().HasIndex(w => new { w.Status, w.CreatedAt });
        builder.Entity<WorkOrder>().HasIndex(w => new { w.AssignedTechnicianId, w.Status });
        builder.Entity<WorkOrder>().HasIndex(w => w.TicketId);
        builder.Entity<WorkOrder>().HasIndex(w => w.OutageId);
        builder.Entity<WorkOrder>().HasIndex(w => w.AssetId);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.Ticket).WithMany().HasForeignKey(w => w.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.Outage).WithMany().HasForeignKey(w => w.OutageId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.Asset).WithMany().HasForeignKey(w => w.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.ServicePoint).WithMany().HasForeignKey(w => w.ServicePointId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.Department).WithMany().HasForeignKey(w => w.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.Region).WithMany().HasForeignKey(w => w.RegionId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<WorkOrder>()
            .HasOne(w => w.AssignedTechnician).WithMany().HasForeignKey(w => w.AssignedTechnicianId).OnDelete(DeleteBehavior.Restrict);

        // MaintenanceSchedule
        builder.Entity<MaintenanceSchedule>().Property(m => m.Status).HasConversion<string>();
        builder.Entity<MaintenanceSchedule>().Property(m => m.MaintenanceType).HasConversion<string>();
        builder.Entity<MaintenanceSchedule>().HasIndex(m => m.ScheduleNumber).IsUnique();
        builder.Entity<MaintenanceSchedule>().HasIndex(m => new { m.ScheduledStart, m.Status });
        builder.Entity<MaintenanceSchedule>().HasIndex(m => m.AssetId);
        builder.Entity<MaintenanceSchedule>()
            .HasOne(m => m.Asset).WithMany().HasForeignKey(m => m.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<MaintenanceSchedule>()
            .HasOne(m => m.Region).WithMany().HasForeignKey(m => m.RegionId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<MaintenanceSchedule>()
            .HasOne(m => m.Department).WithMany().HasForeignKey(m => m.DepartmentId).OnDelete(DeleteBehavior.Restrict);

        // CallLog
        builder.Entity<CallLog>().Property(c => c.Direction).HasConversion<string>();
        builder.Entity<CallLog>().Property(c => c.Channel).HasConversion<string>();
        builder.Entity<CallLog>().Property(c => c.Outcome).HasConversion<string>();
        builder.Entity<CallLog>().HasIndex(c => c.CallReference).IsUnique();
        builder.Entity<CallLog>().HasIndex(c => new { c.CustomerId, c.StartedAt });
        builder.Entity<CallLog>().HasIndex(c => c.StartedAt);
        builder.Entity<CallLog>()
            .HasOne(c => c.Customer).WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<CallLog>()
            .HasOne(c => c.HandledByUser).WithMany().HasForeignKey(c => c.HandledByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<CallLog>()
            .HasOne(c => c.RelatedTicket).WithMany().HasForeignKey(c => c.RelatedTicketId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<CallLog>()
            .HasOne(c => c.RelatedOutage).WithMany().HasForeignKey(c => c.RelatedOutageId).OnDelete(DeleteBehavior.Restrict);

        // OutageNotification
        builder.Entity<OutageNotification>().Property(n => n.Channel).HasConversion<string>();
        builder.Entity<OutageNotification>().Property(n => n.Status).HasConversion<string>();
        builder.Entity<OutageNotification>().HasIndex(n => new { n.CustomerId, n.SentAt });
        builder.Entity<OutageNotification>().HasIndex(n => n.OutageId);
        builder.Entity<OutageNotification>().HasIndex(n => n.MaintenanceScheduleId);
        builder.Entity<OutageNotification>()
            .HasOne(n => n.Outage).WithMany().HasForeignKey(n => n.OutageId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OutageNotification>()
            .HasOne(n => n.MaintenanceSchedule).WithMany().HasForeignKey(n => n.MaintenanceScheduleId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OutageNotification>()
            .HasOne(n => n.Customer).WithMany().HasForeignKey(n => n.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<OutageNotification>()
            .HasOne(n => n.ServiceAccount).WithMany().HasForeignKey(n => n.ServiceAccountId).OnDelete(DeleteBehavior.Restrict);

        // SlaPolicy
        builder.Entity<SlaPolicy>().HasIndex(p => p.PolicyCode).IsUnique();
        builder.Entity<SlaPolicy>().HasIndex(p => new { p.CustomerSegmentId, p.ServiceTypeId, p.PriorityId });
        builder.Entity<SlaPolicy>()
            .HasOne(p => p.CustomerSegment).WithMany().HasForeignKey(p => p.CustomerSegmentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<SlaPolicy>()
            .HasOne(p => p.ServiceType).WithMany().HasForeignKey(p => p.ServiceTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<SlaPolicy>()
            .HasOne(p => p.Priority).WithMany().HasForeignKey(p => p.PriorityId).OnDelete(DeleteBehavior.Restrict);

        // Prevent cascade delete to avoid SQL Server cycles
        foreach (var relationship in builder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }

        builder.Entity<TicketAiAnalysis>().Property(e => e.AnalysisStatus).HasConversion<string>();
        builder.Entity<TicketAiAnalysis>().Property(e => e.ConfidenceLevel).HasConversion<string>();
        builder.Entity<TicketAiAnalysisLog>().Property(e => e.LogLevel).HasConversion<string>();
        builder.Entity<CopilotTraceHistory>()
            .HasIndex(e => new { e.SessionId, e.CaseCode, e.CreatedAt });
        builder.Entity<CopilotTraceHistory>()
            .HasIndex(e => new { e.CaseCode, e.CreatedAt });
        // B4: Standalone CreatedAt index for the retention prune job.
        // The composite indexes above have CreatedAt as the 3rd column so the
        // prune query (WHERE CreatedAt < cutoff) can't use them — full table scan.
        builder.Entity<CopilotTraceHistory>()
            .HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_CopilotTraceHistories_CreatedAt");
    }

}
