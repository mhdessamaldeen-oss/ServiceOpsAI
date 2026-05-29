using ServiceOpsAI.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using Serilog;
using ServiceOpsAI.Services.AI;
using ServiceOpsAI.Services.AI.Contracts;
using ServiceOpsAI.Services.AI.Common;
using ServiceOpsAI.Services.AI.Investigation;
using ServiceOpsAI.Services.AI.Providers;
using ServiceOpsAI.Services.Infrastructure;
using ServiceOpsAI.Services.Notifications;
using ServiceOpsAI.Models.AI;
using System.Text.Json;
using ServiceOpsAI.Mappings;
using ServiceOpsAI.Services.AI.Copilot.Analysis;
using ServiceOpsAI.Services.AI.Copilot.Assessment;
using ServiceOpsAI.Services.AI.Copilot.Suggestions;
using ServiceOpsAI.Services.AI.Copilot.Tools;
using ServiceOpsAI.Services.AI.Copilot.Trace;
using ServiceOpsAI.Hubs;
using SuperAdminCopilot.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// --- Configure Serilog for Daily Files ---
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("Logs/SupportFlow-AI-.txt", rollingInterval: RollingInterval.Day, 
                  outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddSingleton<IRuntimeDatabaseTargetService>(_ => new RuntimeDatabaseTargetService(new RuntimeDatabaseTarget
{
    Provider = RuntimeDatabaseProvider.SqlServer,
    ConnectionString = connectionString
}));
builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    var runtimeDatabaseTarget = serviceProvider.GetRequiredService<IRuntimeDatabaseTargetService>().GetCurrent();
    RuntimeDatabaseConfigurator.Configure(options, runtimeDatabaseTarget);
});
builder.Services.AddDbContextFactory<ApplicationDbContext>(lifetime: ServiceLifetime.Scoped);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddSignalR();
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation()
    .AddJsonOptions(options => {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddAutoMapper(config => { config.AddProfile<MappingProfile>(); });
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDockerService, DockerService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
builder.Services.AddHttpClient();

// ── AI Provider Configuration ────────────────────────────────────────────────
builder.Services.Configure<AiProviderSettings>(builder.Configuration.GetSection(AiProviderSettings.SectionName));

// Register Copilot Text Configuration
builder.Configuration.AddJsonFile("copilot-text.json", optional: true, reloadOnChange: true);
// SuperAdminCopilot — configurable rule files. Hot-reloaded, so operators can add a verb /
// pattern / role mapping by editing the JSON without restarting the host.
builder.Configuration.AddJsonFile("Areas/SuperAdminCopilot/Configuration/write-intent-verbs.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("Areas/SuperAdminCopilot/Configuration/fk-role-patterns.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("Areas/SuperAdminCopilot/Configuration/spec-repair-rules.json", optional: true, reloadOnChange: true);
builder.Services.Configure<CopilotTextSettings>(builder.Configuration);
builder.Services.Configure<CopilotTracePersistenceOptions>(builder.Configuration.GetSection(CopilotTracePersistenceOptions.SectionName));

  builder.Services.AddSingleton<CopilotTextCatalog>();
  builder.Services.AddSingleton<IAiProviderFactory, AiProviderFactory>();
  // Provider retry policies (strategy pattern). GeminiAiProvider takes the rate-limit policy directly;
  // other providers can inject the no-op policy if/when they need to participate in the pipeline.
  builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Providers.Retry.NoRetryPolicy>();
  builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Providers.Retry.GeminiRateLimitPolicy>();
  builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Providers.Retry.GroqRetryPolicy>();
  // Multi-key Gemini pool — rotates across saved API keys so one key's daily 250 RPD cap doesn't
  // bottleneck the copilot. Falls back to legacy single-key in SystemSettings if pool is empty.
  builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Providers.KeyPool.IGeminiKeyPool,
      ServiceOpsAI.Services.AI.Providers.KeyPool.GeminiKeyPool>();
  // Multi-key Groq pool — same pattern, plus persists Groq's authoritative `x-ratelimit-*` headers
  // per key so the UI can show real remaining quota (Groq exposes this; Gemini does not).
  builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Providers.KeyPool.IGroqKeyPool,
      ServiceOpsAI.Services.AI.Providers.KeyPool.GroqKeyPool>();

// ── AI Investigation Services ────────────────────────────────────────────────
builder.Services.AddScoped<TicketContextPreparationService>();
builder.Services.AddScoped<TicketAiPromptBuilder>();
builder.Services.AddScoped<IAiAnalysisService, AiAnalysisService>();
builder.Services.AddScoped<IAiReviewSignalService, AiReviewSignalService>();
builder.Services.AddScoped<IAiInsightsService, AiInsightsService>();
builder.Services.AddScoped<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddScoped<BilingualRetrievalBenchmarkService>();
builder.Services.AddScoped<KnowledgeBaseRagService>();
// Tier-2 hybrid-retrieval helpers — pure algorithmic services (no state, no IO),
// safe as singletons. Consumed by KnowledgeBaseRagService and any future ticket
// hybrid path. Tunables (RrfK, EnableBm25) come from RetrievalTuningSettings UI.
builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Retrieval.IBm25Retriever,
    ServiceOpsAI.Services.AI.Retrieval.Bm25Retriever>();
builder.Services.AddSingleton<ServiceOpsAI.Services.AI.Retrieval.IRrfFuser,
    ServiceOpsAI.Services.AI.Retrieval.RrfFuser>();
builder.Services.AddScoped<ServiceOpsAI.Services.AI.Retrieval.IRecommendationGroundingAuditor,
    ServiceOpsAI.Services.AI.Retrieval.RecommendationGroundingAuditor>();

// CopilotRecommendationAnalyzer: consumed by AiAnalysisController.GetCopilotRecommendation.
// Survives the legacy purge as an "orphan island" along with CopilotTextCatalog + CopilotTextTemplate.
builder.Services.AddScoped<CopilotRecommendationAnalyzer>();

// Shared Copilot host services retained after the SuperAdminCopilot cutover.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CopilotAssessmentHandler>();
builder.Services.AddScoped<CopilotToolRegistry>();
builder.Services.AddScoped<CopilotTraceHistoryStore>();
builder.Services.AddScoped<ServiceOpsAI.Services.AI.Cost.ICostCalculator, ServiceOpsAI.Services.AI.Cost.CostCalculator>();
builder.Services.AddSingleton<ICopilotSuggestionService, CopilotSuggestionService>();
builder.Services.AddScoped<IPipelineTracingService, PipelineTracingService>();
builder.Services.AddScoped<IPipelineVisualizationService, PipelineVisualizationService>();
builder.Services.AddScoped<ICopilotTraceAnalyzer, CopilotTraceAnalyzer>();
builder.Services.AddScoped<IAnswerCorrectnessAssessor, AnswerCorrectnessAssessor>();
builder.Services.AddScoped<ITraceDataInspector, TraceDataInspector>();
// Embedder is wired to the Rag-workload provider — model name comes from the app's
// existing model-selection UI, NOT hardcoded. Whatever you pick for the Rag workload IS
// the embedder. Switch providers in settings → embedder switches with you (re-embedding
// the existing corpus is required when changing models because vectors aren't comparable
// across embedding spaces).
// Register the embedder against BOTH ITextEmbedder (the canonical name; Tier 4.1) and
// ITicketEmbedder (legacy, [Obsolete]) so existing injection sites keep working until they migrate.
#pragma warning disable CS0618 // ITicketEmbedder is obsolete; intentional bridge.
builder.Services.AddSingleton<ProviderTicketEmbedder>();
builder.Services.AddSingleton<ITextEmbedder>(sp => sp.GetRequiredService<ProviderTicketEmbedder>());
builder.Services.AddSingleton<ITicketEmbedder>(sp => sp.GetRequiredService<ProviderTicketEmbedder>());
#pragma warning restore CS0618
builder.Services.AddSingleton<AiAnalysisQueueService>();
builder.Services.AddSingleton<EmbeddingQueueService>();

// ── Super Admin Copilot ─────────────────────────────────────────────────────
// Active in-host copilot endpoint: POST /api/super-admin-copilot/ask
builder.Services.AddSuperAdminCopilot(builder.Configuration);

var app = builder.Build();

// Log the active AI provider at startup
{
    var providerSettings = builder.Configuration.GetSection(AiProviderSettings.SectionName).Get<AiProviderSettings>() ?? new AiProviderSettings();
    Log.Information("AI Provider configured: {ActiveProvider} (Model: {Model})",
        providerSettings.ActiveProvider,
        providerSettings.GetActiveProviderType() switch
        {
            AiProviderType.DockerLocal => providerSettings.DockerLocal.Model,
            AiProviderType.OpenAI => providerSettings.OpenAI.Model,
            AiProviderType.Cloud => providerSettings.Cloud.Model,
            _ => "unknown"
        });
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// --- Localization Middleware ---
var supportedCultures = new[] { "en", "ar" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(supportedCultures[0])
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapHub<CopilotAssessmentHub>("/hubs/copilotAssessment");
app.MapHub<CopilotChatHub>("/hubs/copilotChat");


app.MapRazorPages();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var dbContext = services.GetRequiredService<ApplicationDbContext>();

    // Optional fresh-seed: when Seed:ForceReseed is true, wipe the seed-managed
    // tables BEFORE the seeders run so the AnyAsync() guards return false and
    // every entity is repopulated from scratch (picks up new temporal-bucket
    // distribution, refreshed bill periods, etc.). Identity, lookups, copilot
    // trace history, and the migration ledger are preserved.
    var forceReseed = builder.Configuration.GetValue<bool>("Seed:ForceReseed");
    if (forceReseed)
    {
        await dbContext.Database.MigrateAsync(); // ensure tables exist before DELETE
        await ServiceOpsAI.Data.Seed.SeedReset.WipeAsync(dbContext);
    }

    await DbSeeder.InitializeCoreAsync(services, userManager, roleManager);

    // Phase 06: utility-domain seeder (Syrian customers, bills, departments, tickets).
    // Idempotent — no-ops if Customers already populated.
    await ServiceOpsAI.Data.Seed.ServiceOpsSeeder.SeedAsync(dbContext);

    // Phase 06 depth (billing layer + field ops + customer voice). Runs AFTER the
    // domain seeder above, since Phase 06 rows attach to existing customers/bills/outages.
    // Each table is independently guarded with AnyAsync() so re-running is safe.
    await ServiceOpsAI.Data.DbSeeder.EnsureSeedPhase06PublicAsync(dbContext);

    var aiService = services.GetRequiredService<IAiAnalysisService>();
    await aiService.ResetInterruptedAnalysesAsync();
}

var runRetrievalBenchmark = args.Contains("--run-retrieval-benchmark", StringComparer.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("RUN_RETRIEVAL_BENCHMARK"), "1", StringComparison.OrdinalIgnoreCase);

if (runRetrievalBenchmark)
{
    using var scope = app.Services.CreateScope();
    var benchmarkService = scope.ServiceProvider.GetRequiredService<BilingualRetrievalBenchmarkService>();
    var bucketIndex = Array.FindIndex(args, a => string.Equals(a, "--benchmark-bucket", StringComparison.OrdinalIgnoreCase));
    var bucket = bucketIndex >= 0 && bucketIndex + 1 < args.Length ? args[bucketIndex + 1] : null;
    var result = await benchmarkService.RunAsync(bucket: bucket);

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    return;
}

app.Run();

