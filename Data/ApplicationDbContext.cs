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

    
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Department>().Property(d => d.ServiceType).HasConversion<string>();

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
