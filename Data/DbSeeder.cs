using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AISupportAnalysisPlatform.Data
{
    public static class DbSeeder
    {
        private static readonly string[] BrowserNames = ["Chrome", "Edge", "Firefox", "Safari"];
        private static readonly string[] DesktopOperatingSystems = ["Windows 11", "Windows 10", "macOS", "Ubuntu"];
        private static readonly string[] ServerOperatingSystems = ["Windows Server 2022", "Ubuntu Server 22.04", "RHEL 9"];

        // ─── JSON Deserialization Models ────────────────────────────────────────────
        public class SeedData
        {
            public List<SeedTicketRecord> Tickets { get; set; } = new();
            public List<TicketTemplate> Templates { get; set; } = new();
            public List<TicketTemplate> ArabicTemplates { get; set; } = new();
            public Dictionary<string, string> Attachments { get; set; } = new();
            public List<string> Comments { get; set; } = new();
            public List<string> ArabicComments { get; set; } = new();
            public List<string> Resolutions { get; set; } = new();
            public List<string> ArabicResolutions { get; set; } = new();
        }

        public class SeedTicketRecord
        {
            public string TicketNumber { get; set; } = string.Empty;
            public int Sequence { get; set; }
            public string? Section { get; set; }
            public string? Language { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string? Subcategory { get; set; }
            public string? Entity { get; set; }
            public string? Source { get; set; }
            public string? StatusHint { get; set; }
            public string? PriorityHint { get; set; }
            public string? ProductArea { get; set; }
            public string? EnvironmentName { get; set; }
            public string? BrowserName { get; set; }
            public string? OperatingSystem { get; set; }
            public string? ExternalReferenceId { get; set; }
            public string? ExternalSystemName { get; set; }
            public string? ImpactScope { get; set; }
            public int? AffectedUsersCount { get; set; }
            public string? TechnicalAssessment { get; set; }
            public string? EscalationLevel { get; set; }
            public bool? RequiresManagerReview { get; set; }
            public string? PendingReason { get; set; }
            public List<string> Comments { get; set; } = new();
            public string? ResolutionSummary { get; set; }
            public string? RootCause { get; set; }
            public string? VerificationNotes { get; set; }
            public string? ParentTicketNumber { get; set; }
            public string? CaseType { get; set; }
            public string? CaseChannel { get; set; }
            public string? ServiceCategory { get; set; }
            public string? ServiceName { get; set; }
            public string? OriginalCaseNumber { get; set; }
            public string? ResolutionResponse { get; set; }
            public string? IntentClassification { get; set; }
            public string? FlrFlag { get; set; }
            public string? FcrFlag { get; set; }
            public string? IsFollowUp { get; set; }
            public DateTime? DateOpened { get; set; }
            public DateTime? DateClosed { get; set; }
            public List<string> Attachments { get; set; } = new();
        }

        public class TicketTemplate
        {
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public List<string> Attachments { get; set; } = new();
        }

        private sealed class TicketOperationalProfile
        {
            public required string ProductArea { get; init; }
            public required string EnvironmentName { get; init; }
            public string? BrowserName { get; init; }
            public required string OperatingSystem { get; init; }
            public required string ExternalReferenceId { get; init; }
            public string? ExternalSystemName { get; init; }
            public required string ImpactScope { get; init; }
            public required int AffectedUsersCount { get; init; }
        }

        private static TicketOperationalProfile BuildOperationalProfile(TicketTemplate template, int ticketIndex, Random rng)
        {
            var title = template.Title;
            var description = template.Description;
            var combinedText = $"{title} {description}".ToLowerInvariant();

            var productArea = template.Category switch
            {
                "Authentication" => "Identity and Access",
                "Authorization / Access" => "Identity and Access",
                "Attachment / File Handling" => "Document Processing",
                "Reporting" => "Reporting and Analytics",
                "Integration" => "External Integrations",
                "Performance" => "Platform Performance",
                "UI / UX" => "Portal Experience",
                "Network" => "Connectivity Services",
                _ when combinedText.Contains("dashboard") => "Dashboard Workspace",
                _ when combinedText.Contains("ticket") => "Ticket Operations",
                _ => "Core Support Platform"
            };

            var environmentName = combinedText.Contains("uat") || combinedText.Contains("staging")
                ? "UAT"
                : combinedText.Contains("deployment") || combinedText.Contains("nightly") || combinedText.Contains("production")
                    ? "Production"
                    : combinedText.Contains("export")
                        ? "Reporting Cluster"
                        : "Production";

            var browserName = BrowserNames.FirstOrDefault(browser => combinedText.Contains(browser.ToLowerInvariant()))
                ?? (combinedText.Contains("ui") || combinedText.Contains("login page") ? BrowserNames[rng.Next(BrowserNames.Length)] : null);

            var operatingSystem = template.Category is "Performance" or "Integration"
                ? ServerOperatingSystems[rng.Next(ServerOperatingSystems.Length)]
                : browserName == "Safari"
                    ? "macOS"
                    : DesktopOperatingSystems[rng.Next(DesktopOperatingSystems.Length)];

            var externalSystemName =
                combinedText.Contains("hr") ? "HR Sync API" :
                combinedText.Contains("inventory") ? "Inventory Gateway" :
                combinedText.Contains("sso") ? "SSO Identity Provider" :
                combinedText.Contains("excel") ? "Excel Export Service" :
                combinedText.Contains("upload") || combinedText.Contains("csv") ? "Upload Processing API" :
                combinedText.Contains("dashboard") ? "Dashboard API" :
                combinedText.Contains("report") ? "Reporting Engine" :
                null;

            var impactScope =
                combinedText.Contains("multiple") || combinedText.Contains("multi-system") || combinedText.Contains("cross-entity") ? "Cross-Entity" :
                combinedText.Contains("several users") || combinedText.Contains("users") ? "Department" :
                combinedText.Contains("team") ? "Team" :
                "Single User";

            var affectedUsersCount = impactScope switch
            {
                "Cross-Entity" => rng.Next(80, 250),
                "Department" => rng.Next(15, 80),
                "Team" => rng.Next(5, 20),
                _ => rng.Next(1, 4)
            };

            return new TicketOperationalProfile
            {
                ProductArea = productArea,
                EnvironmentName = environmentName,
                BrowserName = browserName,
                OperatingSystem = operatingSystem,
                ExternalReferenceId = $"EXT-2026-{ticketIndex:D6}",
                ExternalSystemName = externalSystemName,
                ImpactScope = impactScope,
                AffectedUsersCount = affectedUsersCount
            };
        }

        private static TicketOperationalProfile BuildArabicOperationalProfile(TicketTemplate template, int ticketIndex, Random rng)
        {
            var combinedText = $"{template.Title} {template.Description}";
            var productArea = template.Category switch
            {
                "Authentication" => "إدارة الهوية والصلاحيات",
                "Authorization / Access" => "إدارة الهوية والصلاحيات",
                "Attachment / File Handling" => "معالجة الملفات والمرفقات",
                "Reporting" => "التقارير والتحليلات",
                "Integration" => "التكامل مع الأنظمة الخارجية",
                "Performance" => "أداء المنصة",
                "UI / UX" => "بوابة المستخدم وتجربة الواجهة",
                "Notification" => "الإشعارات والتنبيهات",
                "Workflow" => "سير العمل والتصعيد",
                _ => "منصة الدعم الأساسية"
            };

            var environmentName =
                combinedText.Contains("الاختبار") || combinedText.Contains("تجريبي") ? "UAT" :
                combinedText.Contains("التطوير") ? "Development" :
                combinedText.Contains("التقارير") ? "Reporting Cluster" :
                "Production";

            var browserName =
                combinedText.Contains("كروم") ? "Chrome" :
                combinedText.Contains("إيدج") ? "Edge" :
                combinedText.Contains("فايرفوكس") ? "Firefox" :
                combinedText.Contains("سفاري") ? "Safari" :
                (combinedText.Contains("المتصفح") || combinedText.Contains("الواجهة") ? BrowserNames[rng.Next(BrowserNames.Length)] : null);

            var operatingSystem =
                template.Category is "Performance" or "Integration"
                    ? ServerOperatingSystems[rng.Next(ServerOperatingSystems.Length)]
                    : browserName == "Safari"
                        ? "macOS"
                        : DesktopOperatingSystems[rng.Next(DesktopOperatingSystems.Length)];

            var externalSystemName =
                combinedText.Contains("الموارد البشرية") ? "HR Sync API" :
                combinedText.Contains("الهوية") || combinedText.Contains("نفاذ") || combinedText.Contains("الدخول الموحد") ? "SSO Identity Provider" :
                combinedText.Contains("إكسل") || combinedText.Contains("excel") ? "Excel Export Service" :
                combinedText.Contains("واجهة برمجة") || combinedText.Contains("تكامل") ? "Integration Gateway" :
                combinedText.Contains("الرفع") || combinedText.Contains("المرفقات") ? "Upload Processing API" :
                combinedText.Contains("التقارير") ? "Reporting Engine" :
                null;

            var impactScope =
                combinedText.Contains("جميع") || combinedText.Contains("عدة جهات") || combinedText.Contains("أكثر من جهة") ? "Cross-Entity" :
                combinedText.Contains("الإدارة") || combinedText.Contains("القسم") || combinedText.Contains("الموظفين") ? "Department" :
                combinedText.Contains("الفريق") ? "Team" :
                "Single User";

            var affectedUsersCount = impactScope switch
            {
                "Cross-Entity" => rng.Next(80, 250),
                "Department" => rng.Next(15, 80),
                "Team" => rng.Next(5, 20),
                _ => rng.Next(1, 4)
            };

            return new TicketOperationalProfile
            {
                ProductArea = productArea,
                EnvironmentName = environmentName,
                BrowserName = browserName,
                OperatingSystem = operatingSystem,
                ExternalReferenceId = $"AR-EXT-2026-{ticketIndex:D6}",
                ExternalSystemName = externalSystemName,
                ImpactScope = impactScope,
                AffectedUsersCount = affectedUsersCount
            };
        }

        // ─── Main Seed Method ───────────────────────────────────────────────────────
        public static async Task InitializeCoreAsync(IServiceProvider serviceProvider, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();

            if (context.Database.IsSqlServer())
                await context.Database.MigrateAsync();

            // Cleanup malformed legacy settings
            var legacySetting = await context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DefaultTheme);
            if (legacySetting != null && !int.TryParse(legacySetting.Value, out _))
            {
                context.SystemSettings.Remove(legacySetting);
                await context.SaveChangesAsync();
            }

            // Seed Roles
            foreach (var role in new[] { RoleNames.Admin, RoleNames.SupportAgent, RoleNames.EndUser, RoleNames.Viewer })
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));

            // Seed Entities
            if (!context.Entitys.Any())
            {
                context.Entitys.AddRange(
                    new Entity { Name = "Entity of Health" },
                    new Entity { Name = "Ministry of Interior" },
                    new Entity { Name = "Entity of Education" },
                    new Entity { Name = "Entity of Technologies" }
                );
                await context.SaveChangesAsync();
            }

            // Seed Users
            if (!userManager.Users.Any())
            {
                var depts = context.Entitys.ToList();
                var healthDept  = depts.First(d => d.Name.Contains("Health"));
                var interiorDept = depts.First(d => d.Name.Contains("Interior"));
                var eduDept     = depts.First(d => d.Name.Contains("Education"));
                var techDept    = depts.First(d => d.Name.Contains("Technologies"));

                var admin = new ApplicationUser { UserName = "admin@tech.local", Email = "admin@tech.local", FirstName = "Admin", LastName = "Specialist", EmailConfirmed = true, EntityId = techDept.Id };
                await userManager.CreateAsync(admin, "Admin@123");
                await userManager.AddToRoleAsync(admin, RoleNames.Admin);

                for (int i = 1; i <= 3; i++) { var u = new ApplicationUser { UserName = $"user{i}@health.local", Email = $"user{i}@health.local", FirstName = "Health", LastName = $"User{i}", EmailConfirmed = true, EntityId = healthDept.Id }; await userManager.CreateAsync(u, "Admin@123"); await userManager.AddToRoleAsync(u, RoleNames.EndUser); }
                for (int i = 1; i <= 3; i++) { var u = new ApplicationUser { UserName = $"user{i}@interior.local", Email = $"user{i}@interior.local", FirstName = "Interior", LastName = $"User{i}", EmailConfirmed = true, EntityId = interiorDept.Id }; await userManager.CreateAsync(u, "Admin@123"); await userManager.AddToRoleAsync(u, RoleNames.EndUser); }
                for (int i = 1; i <= 3; i++) { var u = new ApplicationUser { UserName = $"user{i}@education.local", Email = $"user{i}@education.local", FirstName = "Education", LastName = $"User{i}", EmailConfirmed = true, EntityId = eduDept.Id }; await userManager.CreateAsync(u, "Admin@123"); await userManager.AddToRoleAsync(u, RoleNames.EndUser); }
                for (int i = 1; i <= 3; i++) { var a = new ApplicationUser { UserName = $"agent{i}@tech.local", Email = $"agent{i}@tech.local", FirstName = "Tech", LastName = $"Specialist{i}", EmailConfirmed = true, EntityId = techDept.Id }; await userManager.CreateAsync(a, "Admin@123"); await userManager.AddToRoleAsync(a, RoleNames.SupportAgent); }
            }

            // Seed Categories
            foreach (var catName in new[] { "Hardware","Software Installation","Network","Performance","Access Request","Authentication","Authorization / Access","UI / UX","Data Issue","Reporting","Notification","Workflow","Attachment / File Handling","Integration","Account Management","Unknown" })
                if (!await context.TicketCategories.AnyAsync(c => c.Name == catName))
                    context.TicketCategories.Add(new TicketCategory { Name = catName });
            await context.SaveChangesAsync();

            // Seed Priorities
            foreach (var sp in new[] { new TicketPriority { Name = TicketPriorityNames.Low, Level = 1 }, new TicketPriority { Name = TicketPriorityNames.Medium, Level = 2 }, new TicketPriority { Name = TicketPriorityNames.High, Level = 3 }, new TicketPriority { Name = TicketPriorityNames.Critical, Level = 4 } })
                if (!await context.TicketPriorities.AnyAsync(p => p.Name == sp.Name))
                    context.TicketPriorities.Add(sp);
            await context.SaveChangesAsync();

            // Seed Statuses
            if (!context.TicketStatuses.Any())
            {
                context.TicketStatuses.AddRange(
                    new TicketStatus { Name = TicketStatusNames.New, IsClosedState = false },
                    new TicketStatus { Name = TicketStatusNames.Open, IsClosedState = false },
                    new TicketStatus { Name = TicketStatusNames.InProgress, IsClosedState = false },
                    new TicketStatus { Name = TicketStatusNames.Pending, IsClosedState = false },
                    new TicketStatus { Name = TicketStatusNames.Resolved, IsClosedState = true },
                    new TicketStatus { Name = TicketStatusNames.Closed, IsClosedState = true },
                    new TicketStatus { Name = TicketStatusNames.Rejected, IsClosedState = true }
                );
                await context.SaveChangesAsync();
            }

            // Seed Sources
            if (!context.TicketSources.Any())
            {
                context.TicketSources.AddRange(
                    new TicketSource { Name = TicketSourceNames.WebPortal },
                    new TicketSource { Name = TicketSourceNames.Email },
                    new TicketSource { Name = TicketSourceNames.Phone },
                    new TicketSource { Name = TicketSourceNames.InternalRequest }
                );
                await context.SaveChangesAsync();
            }

            // Seed System Themes — every theme must have a complete, conflict-free color scale
            if (!context.CustomThemes.Any())
            {
                context.CustomThemes.AddRange(
                    // ── DARK ──
                    new CustomTheme { Name="Emerald Obsidian", SystemIdentifier="emerald-obsidian", IsSystemTheme=true,
                        PrimaryColor="#10b981", PrimaryContrastText="#000000",
                        BgMain="#0a0a0a", BgCard="#141416", BgSurfaceAlt="#1a1a1d", BgElevated="#1f1f23",
                        BgSidebar="#000000", BgHeader="#000000",
                        TextMain="#ffffff", TextMuted="#a1a1aa", BorderColor="#27272a",
                        SuccessColor="#34d399", WarningColor="#fbbf24", DangerColor="#f87171", InfoColor="#60a5fa",
                        ShadowColor="rgba(0, 0, 0, 0.45)" },

                    new CustomTheme { Name="Solar Ember", SystemIdentifier="solar-ember", IsSystemTheme=true,
                        PrimaryColor="#f59e0b", PrimaryContrastText="#0b0b0b",
                        BgMain="#111827", BgCard="#1f2937", BgSurfaceAlt="#273345", BgElevated="#2c3a4f",
                        BgSidebar="#0d1421", BgHeader="#0d1421",
                        TextMain="#f9fafb", TextMuted="#9ca3af", BorderColor="#374151",
                        SuccessColor="#34d399", WarningColor="#fbbf24", DangerColor="#f87171", InfoColor="#60a5fa",
                        ShadowColor="rgba(0, 0, 0, 0.4)" },

                    new CustomTheme { Name="Midnight Azure", SystemIdentifier="midnight-azure", IsSystemTheme=true,
                        PrimaryColor="#3b82f6", PrimaryContrastText="#ffffff",
                        BgMain="#020617", BgCard="#0f172a", BgSurfaceAlt="#172033", BgElevated="#1e293b",
                        BgSidebar="#000814", BgHeader="#000814",
                        TextMain="#f8fafc", TextMuted="#94a3b8", BorderColor="#1e293b",
                        SuccessColor="#34d399", WarningColor="#fbbf24", DangerColor="#f87171", InfoColor="#60a5fa",
                        ShadowColor="rgba(0, 0, 0, 0.5)" },

                    new CustomTheme { Name="Cyber Neon", SystemIdentifier="cyber-neon", IsSystemTheme=true,
                        PrimaryColor="#f97316", PrimaryContrastText="#0b0b0b",
                        BgMain="#0f0f0f", BgCard="#1a1a1a", BgSurfaceAlt="#212124", BgElevated="#26262b",
                        BgSidebar="#000000", BgHeader="#000000",
                        TextMain="#ffffff", TextMuted="#a1a1aa", BorderColor="#27272a",
                        SuccessColor="#34d399", WarningColor="#fbbf24", DangerColor="#f87171", InfoColor="#60a5fa",
                        ShadowColor="rgba(0, 0, 0, 0.5)" },

                    // Deep Forest — FIXED: muted was brighter than primary; primary lifted; muted desaturated
                    new CustomTheme { Name="Deep Forest", SystemIdentifier="deep-forest", IsSystemTheme=true,
                        PrimaryColor="#10b981", PrimaryContrastText="#022c22",
                        BgMain="#022c22", BgCard="#064e3b", BgSurfaceAlt="#0a5e48", BgElevated="#0f6e54",
                        BgSidebar="#011d17", BgHeader="#011d17",
                        TextMain="#ecfdf5", TextMuted="#a7d4c0", BorderColor="#065f46",
                        SuccessColor="#34d399", WarningColor="#fbbf24", DangerColor="#fca5a5", InfoColor="#7dd3fc",
                        ShadowColor="rgba(0, 0, 0, 0.45)" },

                    // ── LIGHT ──
                    new CustomTheme { Name="Indigo Horizon", SystemIdentifier="indigo-horizon", IsSystemTheme=true,
                        PrimaryColor="#6366f1", PrimaryContrastText="#ffffff",
                        BgMain="#f1f5f9", BgCard="#ffffff", BgSurfaceAlt="#f8fafc", BgElevated="#ffffff",
                        BgSidebar="#ffffff", BgHeader="#fafbfc",
                        TextMain="#1e293b", TextMuted="#64748b", BorderColor="#e2e8f0",
                        SuccessColor="#16a34a", WarningColor="#d97706", DangerColor="#dc2626", InfoColor="#2563eb",
                        ShadowColor="rgba(15, 23, 42, 0.08)" },

                    // Slate Alpine — FIXED: primary was a neutral gray; replaced with sky accent
                    new CustomTheme { Name="Slate Alpine", SystemIdentifier="slate-alpine", IsSystemTheme=true,
                        PrimaryColor="#0ea5e9", PrimaryContrastText="#ffffff",
                        BgMain="#f1f5f9", BgCard="#ffffff", BgSurfaceAlt="#f8fafc", BgElevated="#ffffff",
                        BgSidebar="#ffffff", BgHeader="#f8fafc",
                        TextMain="#0f172a", TextMuted="#475569", BorderColor="#cbd5e1",
                        SuccessColor="#16a34a", WarningColor="#d97706", DangerColor="#dc2626", InfoColor="#2563eb",
                        ShadowColor="rgba(15, 23, 42, 0.08)" },

                    // Rose Quartz — FIXED: text was three competing reds; now neutral slates with pink only as primary
                    new CustomTheme { Name="Rose Quartz", SystemIdentifier="rose-quartz", IsSystemTheme=true,
                        PrimaryColor="#ec4899", PrimaryContrastText="#ffffff",
                        BgMain="#fdf2f4", BgCard="#ffffff", BgSurfaceAlt="#fef7f9", BgElevated="#ffffff",
                        BgSidebar="#ffffff", BgHeader="#fef7f9",
                        TextMain="#1f2937", TextMuted="#64748b", BorderColor="#fbcfe8",
                        SuccessColor="#16a34a", WarningColor="#d97706", DangerColor="#dc2626", InfoColor="#2563eb",
                        ShadowColor="rgba(131, 24, 67, 0.08)" },

                    new CustomTheme { Name="Emerald Light", SystemIdentifier="emerald-light", IsSystemTheme=true,
                        PrimaryColor="#10b981", PrimaryContrastText="#ffffff",
                        BgMain="#f1f5f9", BgCard="#ffffff", BgSurfaceAlt="#f8fafc", BgElevated="#ffffff",
                        BgSidebar="#ffffff", BgHeader="#f8fafc",
                        TextMain="#0f172a", TextMuted="#4b5563", BorderColor="#d1d5db",
                        SuccessColor="#16a34a", WarningColor="#d97706", DangerColor="#dc2626", InfoColor="#2563eb",
                        ShadowColor="rgba(15, 23, 42, 0.08)" }
                );
                await context.SaveChangesAsync();
            }

            // Backfill: existing installs may have system themes without the extended tokens — top them up
            // without touching values an admin has explicitly customized.
            await BackfillSystemThemeTokensAsync(context);

            // Seed Copilot Tools
            await SeedCopilotToolsAsync(context);

            // Seed System Settings
            var config = await context.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DefaultTheme);
            if (config == null || !int.TryParse(config.Value, out _))
            {
                if (config != null) context.SystemSettings.Remove(config);
                var emerald = await context.CustomThemes.FirstOrDefaultAsync(t => t.SystemIdentifier == "emerald-obsidian");
                if (emerald != null)
                {
                    context.SystemSettings.Add(new SystemSetting { Key = SettingKeys.DefaultTheme, Value = emerald.Id.ToString() });
                    await context.SaveChangesAsync();
                }
            }
        }

        public static async Task SeedOperationalDataAsync(IServiceProvider serviceProvider, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            await InitializeCoreAsync(serviceProvider, userManager, roleManager);

            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();
            var webHostEnvironment = serviceProvider.GetRequiredService<IWebHostEnvironment>();
            await EnsureSeedTicketsAsync(context, userManager, webHostEnvironment);
        }

        public static async Task PurgeOperationalDataAsync(IServiceProvider serviceProvider)
        {
            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();

            if (context.Database.IsSqlServer())
                await context.Database.MigrateAsync();

            await context.TicketAiAnalysisLogs.ExecuteDeleteAsync();

            await context.TicketAiAnalyses.ExecuteDeleteAsync();
            await context.TicketSemanticEmbeddings.ExecuteDeleteAsync();
            await context.TicketAttachments.ExecuteDeleteAsync();
            await context.TicketComments.ExecuteDeleteAsync();
            await context.TicketHistories.ExecuteDeleteAsync();
            await context.Notifications.ExecuteDeleteAsync();
            await context.Tickets.ExecuteDeleteAsync();
        }

        public static async Task SeedAsync(IServiceProvider serviceProvider, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            await SeedOperationalDataAsync(serviceProvider, userManager, roleManager);
        }

        private static async Task EnsureSeedTicketsAsync(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment env)
        {
            var seedDataPath = Path.Combine(env.ContentRootPath, "Data", "SeedData", "seed-data.json");
            if (!File.Exists(seedDataPath))
            {
                Console.WriteLine($"⚠ Seed data file not found: {seedDataPath}");
                return;
            }

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            SeedData seedData;
            try
            {
                var seedJson = await File.ReadAllTextAsync(seedDataPath);
                seedData = JsonSerializer.Deserialize<SeedData>(seedJson, jsonOptions)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ SEED ERROR: Failed to deserialize seed-data.json");
                Console.WriteLine($"Message: {ex.Message}");
                if (ex.InnerException != null) Console.WriteLine($"Inner: {ex.InnerException.Message}");
                return;
            }

            var sourceAttachmentsDir = Path.Combine(env.ContentRootPath, "Data", "SeedData", "attachments");
            var targetAttachmentsDir = Path.Combine(env.WebRootPath, "uploads", "seed");
            if (!Directory.Exists(targetAttachmentsDir))
            {
                Directory.CreateDirectory(targetAttachmentsDir);
            }

            if (Directory.Exists(sourceAttachmentsDir))
            {
                foreach (var file in Directory.GetFiles(sourceAttachmentsDir))
                {
                    var destFile = Path.Combine(targetAttachmentsDir, Path.GetFileName(file));
                    File.Copy(file, destFile, overwrite: true);
                }
            }

            if (seedData.Tickets.Any())
            {
                var explicitCategoryNames = seedData.Tickets
                    .Select(t => (t.Category ?? string.Empty).Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Length > 100 ? name[..100] : name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var categoryName in explicitCategoryNames)
                {
                    if (!await context.TicketCategories.AnyAsync(c => c.Name == categoryName))
                    {
                        context.TicketCategories.Add(new TicketCategory { Name = categoryName });
                    }
                }

                await context.SaveChangesAsync();

                if (!context.Tickets.Any())
                {
                    await SeedExplicitTicketCorpusAsync(context, userManager, seedData, sourceAttachmentsDir);
                }

                if (!context.Notifications.Any())
                {
                    var rngNotifications = new Random(12345);
                    foreach (var user in userManager.Users.ToList())
                    {
                        for (int n = 0; n < rngNotifications.Next(1, 5); n++)
                        {
                            context.Notifications.Add(new Notification
                            {
                                Title = "Alert: " + (rngNotifications.Next(0, 2) == 0 ? "Ticket Update" : "Personnel Mention"),
                                Message = "Important update regarding the Case Intelligence workflow. Please track activity.",
                                UserId = user.Id,
                                CreatedAt = DateTime.UtcNow.AddMinutes(-rngNotifications.Next(1, 1440)),
                                IsRead = rngNotifications.Next(0, 2) == 0
                            });
                        }
                    }

                    await context.SaveChangesAsync();
                }

                return;
            }

            var rng = new Random(12345);
            var categories = context.TicketCategories.ToList();
            var priorities = context.TicketPriorities.Select(p => p.Id).ToList();
            var statuses = context.TicketStatuses.ToList();
            var sources = context.TicketSources.Select(s => s.Id).ToList();
            var assignees = (await userManager.GetUsersInRoleAsync(RoleNames.SupportAgent)).ToList();
            var creators = (await userManager.GetUsersInRoleAsync(RoleNames.EndUser)).ToList();
            var attachmentMeta = seedData.Attachments;

            if (!context.Tickets.Any())
            {
                await SeedTicketBatchAsync(
                    context,
                    seedData.Templates,
                    seedData.Comments,
                    seedData.Resolutions,
                    categories,
                    priorities,
                    statuses,
                    sources,
                    assignees,
                    creators,
                    attachmentMeta,
                    sourceAttachmentsDir,
                    rng,
                    total: 1000,
                    ticketPrefix: "TCK-2026-",
                    categoryProfileBuilder: BuildOperationalProfile,
                    pendingReason: "Awaiting user feedback regarding the requested logs.",
                    batchLabel: "English");
            }

            var arabicExistingCount = await context.Tickets.CountAsync(t => t.TicketNumber.StartsWith("TCK-AR-2026-"));
            if (arabicExistingCount < 1000)
            {
                var arabicMissingCount = 1000 - arabicExistingCount;
                await SeedTicketBatchAsync(
                    context,
                    seedData.ArabicTemplates,
                    seedData.ArabicComments,
                    seedData.ArabicResolutions,
                    categories,
                    priorities,
                    statuses,
                    sources,
                    assignees,
                    creators,
                    attachmentMeta,
                    sourceAttachmentsDir,
                    rng,
                    total: arabicMissingCount,
                    ticketPrefix: "TCK-AR-2026-",
                    categoryProfileBuilder: BuildArabicOperationalProfile,
                    pendingReason: "بانتظار تزويد فريق الدعم بالمعلومات أو السجلات المطلوبة.",
                    batchLabel: "Arabic",
                    startSequence: arabicExistingCount + 1);
            }

            if (!context.Notifications.Any())
            {
                foreach (var user in userManager.Users.ToList())
                {
                    for (int n = 0; n < rng.Next(1, 5); n++)
                    {
                        context.Notifications.Add(new Notification
                        {
                            Title = "Alert: " + (rng.Next(0, 2) == 0 ? "Ticket Update" : "Personnel Mention"),
                            Message = "Important update regarding the Case Intelligence workflow. Please track activity.",
                            UserId = user.Id,
                            CreatedAt = DateTime.UtcNow.AddMinutes(-rng.Next(1, 1440)),
                            IsRead = rng.Next(0, 2) == 0
                        });
                    }
                }

                await context.SaveChangesAsync();
            }
        }

        private static async Task SeedExplicitTicketCorpusAsync(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            SeedData seedData,
            string sourceAttachmentsDir)
        {
            if (!seedData.Tickets.Any())
            {
                return;
            }

            var categories = await context.TicketCategories.ToListAsync();
            var priorities = await context.TicketPriorities.OrderBy(p => p.Level).ToListAsync();
            var statuses = await context.TicketStatuses.ToListAsync();
            var sources = await context.TicketSources.ToListAsync();
            var assignees = (await userManager.GetUsersInRoleAsync(RoleNames.SupportAgent)).ToList();
            var creators = (await userManager.GetUsersInRoleAsync(RoleNames.EndUser)).ToList();
            var admins = (await userManager.GetUsersInRoleAsync(RoleNames.Admin)).ToList();
            var entities = await context.Entities.ToListAsync();
            var rng = new Random(12345);
            var creatorsByEntityId = creators
                .Where(c => c.EntityId.HasValue)
                .GroupBy(c => c.EntityId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
            var escalationOwners = admins
                .Concat(assignees)
                .GroupBy(user => user.Id)
                .Select(group => group.First())
                .ToList();
            var createdTicketsByNumber = new Dictionary<string, Ticket>(StringComparer.OrdinalIgnoreCase);

            var defaultStatus = statuses.First(s => s.Name == TicketStatusNames.New);
            var openStatus = statuses.First(s => s.Name == TicketStatusNames.Open);
            var inProgressStatus = statuses.First(s => s.Name == TicketStatusNames.InProgress);
            var pendingStatus = statuses.First(s => s.Name == TicketStatusNames.Pending);
            var resolvedStatus = statuses.First(s => s.Name == TicketStatusNames.Resolved);
            var closedStatus = statuses.First(s => s.Name == TicketStatusNames.Closed);
            var rejectedStatus = statuses.First(s => s.Name == TicketStatusNames.Rejected);

            foreach (var record in seedData.Tickets.OrderBy(t => t.Sequence).ThenBy(t => t.TicketNumber))
            {
                var entity = ResolveEntity(record, entities) ?? entities[rng.Next(entities.Count)];
                var entityCreators = creatorsByEntityId.GetValueOrDefault(entity.Id, creators);
                var creator = entityCreators[rng.Next(entityCreators.Count)];
                var assignee = assignees[rng.Next(assignees.Count)];
                var categoryName = string.IsNullOrWhiteSpace(record.Category) ? "Unknown" : record.Category.Trim();
                if (categoryName.Length > 100) categoryName = categoryName[..100];
                var category = categories.FirstOrDefault(c => c.Name == categoryName) ?? categories.First();

                var openedAt = record.DateOpened ?? DateTime.UtcNow.AddDays(-rng.Next(30, 180));
                var status = DetermineExplicitStatus(
                    record,
                    defaultStatus,
                    openStatus,
                    inProgressStatus,
                    pendingStatus,
                    resolvedStatus,
                    closedStatus,
                    rejectedStatus);
                var resolvedAt = record.DateClosed;
                if (!resolvedAt.HasValue && (status.Name == TicketStatusNames.Resolved || status.Name == TicketStatusNames.Closed))
                {
                    resolvedAt = openedAt.AddHours(rng.Next(8, 96));
                }

                var priority = DeterminePriority(record, priorities);
                var source = DetermineSource(record, sources);
                var profile = BuildExplicitTicketProfile(record, openedAt, rng);
                var escalationActive = !string.IsNullOrWhiteSpace(record.EscalationLevel);
                var requiresManagerReview = record.RequiresManagerReview ??
                    escalationActive ||
                    string.Equals(status.Name, TicketStatusNames.Closed, StringComparison.OrdinalIgnoreCase);
                var escalationOwner = escalationActive
                    ? escalationOwners[rng.Next(escalationOwners.Count)]
                    : null;
                var resolutionApprover = string.Equals(status.Name, TicketStatusNames.Closed, StringComparison.OrdinalIgnoreCase)
                    ? admins[rng.Next(admins.Count)]
                    : null;
                var parentTicket = !string.IsNullOrWhiteSpace(record.ParentTicketNumber) &&
                    createdTicketsByNumber.TryGetValue(record.ParentTicketNumber, out var existingParent)
                    ? existingParent
                    : null;

                DateTime? firstRespondedAt = status.Name == TicketStatusNames.New
                    ? null
                    : openedAt.AddMinutes(rng.Next(10, 480));

                var firstResponseDueAt = openedAt.AddHours(4);
                var resolutionDueAt = openedAt.AddDays(2);
                var updatedAt = resolvedAt ?? firstRespondedAt ?? openedAt.AddMinutes(rng.Next(15, 180));
                var resolutionApprovedAt = resolutionApprover != null && resolvedAt.HasValue
                    ? resolvedAt.Value.AddHours(rng.Next(1, 8))
                    : (DateTime?)null;

                var ticket = new Ticket
                {
                    TicketNumber = record.TicketNumber,
                    Title = record.Title.Trim(),
                    Description = record.Description.Trim(),
                    CategoryId = category.Id,
                    PriorityId = priority.Id,
                    StatusId = status.Id,
                    SourceId = source.Id,
                    EntityId = entity.Id,
                    AssignedToUserId = assignee.Id,
                    CreatedByUserId = creator.Id,
                    CreatedAt = openedAt,
                    UpdatedAt = updatedAt,
                    DueDate = resolutionDueAt,
                    ProductArea = profile.ProductArea,
                    EnvironmentName = profile.EnvironmentName,
                    BrowserName = profile.BrowserName,
                    OperatingSystem = profile.OperatingSystem,
                    ExternalReferenceId = profile.ExternalReferenceId,
                    ExternalSystemName = profile.ExternalSystemName,
                    ImpactScope = profile.ImpactScope,
                    AffectedUsersCount = profile.AffectedUsersCount,
                    TechnicalAssessment = BuildTechnicalAssessment(record),
                    EscalationLevel = Truncate(record.EscalationLevel, 50),
                    EscalatedAt = escalationOwner != null ? openedAt.AddHours(rng.Next(1, 18)) : null,
                    EscalatedToUserId = escalationOwner?.Id,
                    FirstResponseDueAt = firstResponseDueAt,
                    ResolutionDueAt = resolutionDueAt,
                    FirstRespondedAt = firstRespondedAt,
                    ResolvedAt = resolvedAt,
                    ClosedAt = status.Name == TicketStatusNames.Closed ? (resolvedAt ?? openedAt.AddDays(1)) : null,
                    IsSlaBreached = (firstRespondedAt.HasValue && firstRespondedAt > firstResponseDueAt) ||
                                    (resolvedAt.HasValue && resolvedAt > resolutionDueAt),
                    ResolutionSummary = Truncate(record.ResolutionSummary ?? record.ResolutionResponse, 500),
                    ResolvedByUserId = resolvedAt.HasValue ? assignee.Id : null,
                    RootCause = Truncate(record.RootCause, 1500),
                    VerificationNotes = Truncate(record.VerificationNotes, 1500),
                    ResolutionApprovedByUserId = resolutionApprover?.Id,
                    ResolutionApprovedAt = resolutionApprovedAt,
                    PendingReason = status.Name == TicketStatusNames.Pending
                        ? Truncate(
                            record.PendingReason ??
                            (record.Language == "Arabic"
                                ? "بانتظار تحديث إضافي من مقدم البلاغ أو الجهة الفنية."
                                : "Awaiting additional update from the reporter or technical owner."),
                            500)
                        : null,
                    RequiresManagerReview = requiresManagerReview,
                    ParentTicketId = parentTicket?.Id
                };

                context.Tickets.Add(ticket);
                await context.SaveChangesAsync();
                createdTicketsByNumber[ticket.TicketNumber] = ticket;

                var commentLines = record.Comments
                    .Where(comment => !string.IsNullOrWhiteSpace(comment))
                    .Take(5)
                    .ToList();

                foreach (var comment in commentLines)
                {
                    context.TicketComments.Add(new TicketComment
                    {
                        TicketId = ticket.Id,
                        Content = Truncate(comment, 1800) ?? string.Empty,
                        CreatedByUserId = assignee.Id,
                        CreatedAt = openedAt.AddHours(rng.Next(1, 36))
                    });
                }

                var finalComment = record.ResolutionResponse ?? record.ResolutionSummary;
                if (!string.IsNullOrWhiteSpace(finalComment))
                {
                    context.TicketComments.Add(new TicketComment
                    {
                        TicketId = ticket.Id,
                        Content = Truncate(finalComment, 1800) ?? string.Empty,
                        CreatedByUserId = assignee.Id,
                        CreatedAt = resolvedAt ?? openedAt.AddHours(8)
                    });
                }

                foreach (var fileName in record.Attachments.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var sourceFile = Path.Combine(sourceAttachmentsDir, fileName);
                    if (!File.Exists(sourceFile))
                    {
                        continue;
                    }

                    context.TicketAttachments.Add(new TicketAttachment
                    {
                        TicketId = ticket.Id,
                        FileName = fileName,
                        FilePath = $"/uploads/seed/{fileName}",
                        ContentType = seedData.Attachments.GetValueOrDefault(fileName, "application/octet-stream"),
                        FileSize = new FileInfo(sourceFile).Length,
                        UploadedByUserId = creator.Id,
                        UploadedAt = openedAt.AddMinutes(rng.Next(5, 90))
                    });
                }

                await context.SaveChangesAsync();
            }

            Console.WriteLine($"✅ Seeded {seedData.Tickets.Count} explicit tickets from Data/SeedData/seed-data.json");
        }

        private static TicketPriority DeterminePriority(SeedTicketRecord record, List<TicketPriority> priorities)
        {
            if (!string.IsNullOrWhiteSpace(record.PriorityHint))
            {
                var hintedLevel = record.PriorityHint.Trim().ToLowerInvariant() switch
                {
                    "critical" => 4,
                    "high" => 3,
                    "low" => 1,
                    _ => 2
                };

                return priorities
                    .OrderBy(p => Math.Abs(p.Level - hintedLevel))
                    .ThenByDescending(p => p.Level)
                    .First();
            }

            var text = $"{record.Section} {record.Category} {record.Subcategory} {record.EscalationLevel} {record.Title} {record.Description} {record.PendingReason}".ToLowerInvariant();
            var level = 2;

            if (text.Contains("serious") || text.Contains("critical") || text.Contains("urgent") || text.Contains("danger") ||
                text.Contains("executive") || text.Contains("leadership") || text.Contains("outage") || text.Contains("security") ||
                text.Contains("حادث") || text.Contains("خطر") || text.Contains("طارئ") || text.Contains("مستعجل") ||
                text.Contains("تصعيد للإدارة") || text.Contains("تعطل كامل"))
            {
                level = 4;
            }
            else if (text.Contains("delay") || text.Contains("complaint") || text.Contains("follow up") || text.Contains("follow-up") ||
                     text.Contains("deadline") || text.Contains("backlog") || text.Contains("reopened") ||
                     text.Contains("شكوى") || text.Contains("تأخير") || text.Contains("تصعيد") || text.Contains("مهلة"))
            {
                level = 3;
            }
            else if (string.Equals(record.StatusHint, TicketStatusNames.New, StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(record.EscalationLevel))
            {
                level = 1;
            }

            return priorities
                .OrderBy(p => Math.Abs(p.Level - level))
                .ThenByDescending(p => p.Level)
                .First();
        }

        private static TicketSource DetermineSource(SeedTicketRecord record, List<TicketSource> sources)
        {
            if (!string.IsNullOrWhiteSpace(record.Source))
            {
                var exact = sources.FirstOrDefault(s => string.Equals(s.Name, record.Source, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            var channel = $"{record.Section} {record.Title} {record.Description}".ToLowerInvariant();
            var sourceName =
                channel.Contains("mail") ? TicketSourceNames.Email :
                channel.Contains("phone") ? TicketSourceNames.Phone :
                channel.Contains("web") || channel.Contains("portal") || channel.Contains("browser") || channel.Contains("chat") || channel.Contains("self service") || channel.Contains("self-service") || channel.Contains("website") || channel.Contains("المتصفح") || channel.Contains("البوابة")
                    ? TicketSourceNames.WebPortal
                    : TicketSourceNames.InternalRequest;

            return sources.First(s => s.Name == sourceName);
        }

        private static TicketOperationalProfile BuildExplicitTicketProfile(SeedTicketRecord record, DateTime openedAt, Random rng)
        {
            var text = $"{record.Section} {record.ProductArea} {record.Category} {record.Subcategory} {record.Title} {record.Description}".ToLowerInvariant();
            var impactScope = !string.IsNullOrWhiteSpace(record.ImpactScope)
                ? record.ImpactScope
                : text.Contains("cross-entity") || text.Contains("multiple entities") || text.Contains("عدة جهات") || text.Contains("أكثر من جهة")
                    ? "Cross-Entity"
                    : text.Contains("department") || text.Contains("school") || text.Contains("hospital") || text.Contains("all") || text.Contains("الإدارة") || text.Contains("المدرسة") || text.Contains("المستشفى") || text.Contains("جميع")
                    ? "Department"
                    : text.Contains("team") || text.Contains("family") || text.Contains("group") || text.Contains("الفريق") || text.Contains("مجموعة")
                        ? "Team"
                        : "Single User";

            var affectedUsersCount = record.AffectedUsersCount ?? (impactScope switch
            {
                "Department" => rng.Next(20, 95),
                "Team" => rng.Next(5, 20),
                "Cross-Entity" => rng.Next(80, 250),
                _ => rng.Next(1, 4)
            });

            return new TicketOperationalProfile
            {
                ProductArea = Truncate(record.ProductArea ?? InferProductArea(record.Category), 100) ?? "Complaint Intake",
                EnvironmentName = Truncate(record.EnvironmentName, 50) ?? "Production",
                BrowserName = Truncate(record.BrowserName, 80) ??
                    (text.Contains("portal") || text.Contains("website") || text.Contains("browser") || text.Contains("الموقع") || text.Contains("البوابة")
                        ? BrowserNames[rng.Next(BrowserNames.Length)]
                        : null),
                OperatingSystem = Truncate(record.OperatingSystem, 80) ??
                    ((record.Category is "Performance" or "Integration" or "Network")
                        ? ServerOperatingSystems[rng.Next(ServerOperatingSystems.Length)]
                        : DesktopOperatingSystems[rng.Next(DesktopOperatingSystems.Length)]),
                ExternalReferenceId = Truncate(record.ExternalReferenceId, 120) ?? $"SEED-{openedAt:yyyyMMddHHmmss}",
                ExternalSystemName = Truncate(record.ExternalSystemName ?? InferExternalSystemName(record.Category, text), 120),
                ImpactScope = Truncate(impactScope, 50) ?? "Single User",
                AffectedUsersCount = affectedUsersCount
            };
        }

        private static string BuildTechnicalAssessment(SeedTicketRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.TechnicalAssessment))
            {
                return Truncate(record.TechnicalAssessment.Trim(), 2000) ?? string.Empty;
            }

            var parts = new[]
            {
                record.Section,
                record.Language,
                record.Category,
                record.Subcategory,
                record.Entity,
                record.ProductArea,
                record.EnvironmentName,
                record.ImpactScope,
                record.AffectedUsersCount?.ToString(),
                record.EscalationLevel,
                record.ExternalSystemName,
                record.PendingReason
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());

            return Truncate(string.Join(" | ", parts), 2000) ?? string.Empty;
        }

        private static TicketStatus DetermineExplicitStatus(
            SeedTicketRecord record,
            TicketStatus defaultStatus,
            TicketStatus openStatus,
            TicketStatus inProgressStatus,
            TicketStatus pendingStatus,
            TicketStatus resolvedStatus,
            TicketStatus closedStatus,
            TicketStatus rejectedStatus)
        {
            if (!string.IsNullOrWhiteSpace(record.StatusHint))
            {
                return record.StatusHint.Trim().ToLowerInvariant() switch
                {
                    "open" => openStatus,
                    "in progress" => inProgressStatus,
                    "inprogress" => inProgressStatus,
                    "pending" => pendingStatus,
                    "resolved" => resolvedStatus,
                    "closed" => closedStatus,
                    "rejected" => rejectedStatus,
                    _ => defaultStatus
                };
            }

            if (record.DateClosed.HasValue)
            {
                return closedStatus;
            }

            if (!string.IsNullOrWhiteSpace(record.PendingReason))
            {
                return pendingStatus;
            }

            if (!string.IsNullOrWhiteSpace(record.ResolutionSummary) ||
                !string.IsNullOrWhiteSpace(record.RootCause) ||
                !string.IsNullOrWhiteSpace(record.VerificationNotes))
            {
                return resolvedStatus;
            }

            if (!string.IsNullOrWhiteSpace(record.EscalationLevel) ||
                string.Equals(record.Section, "English / Follow-Up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.Section, "Arabic / Follow-Up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.Section, "English / Escalated Business Impact", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.Section, "Arabic / Escalated Business Impact", StringComparison.OrdinalIgnoreCase))
            {
                return inProgressStatus;
            }

            return openStatus;
        }

        private static Entity? ResolveEntity(SeedTicketRecord record, List<Entity> entities)
        {
            if (!string.IsNullOrWhiteSpace(record.Entity))
            {
                var exact = entities.FirstOrDefault(e => string.Equals(e.Name, record.Entity, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            var combined = $"{record.ProductArea} {record.Title} {record.Description}".ToLowerInvariant();
            if (combined.Contains("health") || combined.Contains("hospital") || combined.Contains("patient") || combined.Contains("auth") || combined.Contains("identity"))
            {
                return entities.FirstOrDefault(e => e.Name.Contains("Health", StringComparison.OrdinalIgnoreCase));
            }

            if (combined.Contains("education") || combined.Contains("school") || combined.Contains("student") || combined.Contains("report"))
            {
                return entities.FirstOrDefault(e => e.Name.Contains("Education", StringComparison.OrdinalIgnoreCase));
            }

            if (combined.Contains("security") || combined.Contains("police") || combined.Contains("access") || combined.Contains("traffic"))
            {
                return entities.FirstOrDefault(e => e.Name.Contains("Interior", StringComparison.OrdinalIgnoreCase));
            }

            return entities.FirstOrDefault(e => e.Name.Contains("Technologies", StringComparison.OrdinalIgnoreCase));
        }

        private static string InferProductArea(string? category) => category switch
        {
            "Hardware" => "Device and Endpoint Operations",
            "Software Installation" => "Desktop Application Delivery",
            "Network" => "Connectivity Services",
            "Performance" => "Platform Performance",
            "Access Request" => "Identity Requests",
            "Authentication" => "Identity and Access",
            "Authorization / Access" => "Identity and Access",
            "UI / UX" => "Portal Experience",
            "Data Issue" => "Data Integrity",
            "Reporting" => "Reporting and Analytics",
            "Notification" => "Messaging and Alerts",
            "Workflow" => "Ticket Operations",
            "Attachment / File Handling" => "Document Processing",
            "Integration" => "Integration Gateway",
            "Account Management" => "Profile and Account Lifecycle",
            _ => "Core Support Platform"
        };

        private static string? InferExternalSystemName(string? category, string text) => category switch
        {
            "Hardware" => "Endpoint Health Service",
            "Software Installation" => "Package Deployment Service",
            "Network" => "Network Gateway",
            "Performance" => "Application Telemetry Hub",
            "Access Request" => "Approval Engine",
            "Authentication" => text.Contains("otp") || text.Contains("mfa") || text.Contains("رمز") ? "SSO Identity Provider" : "Session Cache",
            "Authorization / Access" => "Role Mapping Service",
            "UI / UX" => "Frontend Shell",
            "Data Issue" => "Audit Log Store",
            "Reporting" => "Reporting Engine",
            "Notification" => "Notification Broker",
            "Workflow" => "Queue Orchestrator",
            "Attachment / File Handling" => "Upload Processing API",
            "Integration" => "Integration Gateway",
            "Account Management" => "Profile Service",
            _ => "Core Support API"
        };

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private static async Task SeedTicketBatchAsync(
            ApplicationDbContext context,
            List<TicketTemplate> templates,
            List<string> comments,
            List<string> resolutions,
            List<TicketCategory> categories,
            List<int> priorities,
            List<TicketStatus> statuses,
            List<int> sources,
            List<ApplicationUser> assignees,
            List<ApplicationUser> creators,
            Dictionary<string, string> attachmentMeta,
            string sourceAttachmentsDir,
            Random rng,
            int total,
            string ticketPrefix,
            Func<TicketTemplate, int, Random, TicketOperationalProfile> categoryProfileBuilder,
            string pendingReason,
            string batchLabel,
            int startSequence = 1)
        {
            if (templates.Count == 0 || comments.Count == 0 || resolutions.Count == 0)
            {
                Console.WriteLine($"⚠ Skipping {batchLabel} ticket batch because the seed data is incomplete.");
                return;
            }

            var generated = new List<(Ticket Ticket, TicketTemplate Template)>(total);
            var sequenceEnd = startSequence + total - 1;

            for (int sequence = startSequence; sequence <= sequenceEnd; sequence++)
            {
                var status = statuses[rng.Next(statuses.Count)];
                var createdAt = DateTime.UtcNow.AddDays(-rng.Next(1, 120)).AddHours(-rng.Next(1, 24));
                var firstResponseDue = createdAt.AddHours(4);
                var resolutionDue = createdAt.AddDays(2);

                DateTime? firstRespondedAt = status.Name != TicketStatusNames.New
                    ? createdAt.AddMinutes(rng.Next(5, 600))
                    : null;

                DateTime? resolvedAt = null;
                string? resolutionSummary = null;
                if (status.IsClosedState && (status.Name == TicketStatusNames.Resolved || status.Name == TicketStatusNames.Closed))
                {
                    resolvedAt = createdAt.AddDays(rng.Next(0, 5)).AddHours(rng.Next(1, 23));
                    if (rng.Next(0, 10) > 2)
                    {
                        resolutionSummary = resolutions[rng.Next(resolutions.Count)];
                    }
                }

                var creator = creators[rng.Next(creators.Count)];
                var template = templates[rng.Next(templates.Count)];
                var category = categories.FirstOrDefault(c => c.Name == template.Category) ?? categories.First();
                var profile = categoryProfileBuilder(template, sequence, rng);

                var title = template.Title;
                var body = template.Description;

                if (!ticketPrefix.Contains("-AR-") && rng.Next(0, 10) > 6)
                {
                    title = $"[{rng.Next(1000, 9999)}] {title} - Urgent";
                }

                if (ticketPrefix.Contains("-AR-"))
                {
                    if (rng.Next(0, 10) > 5)
                    {
                        body += $"\n\nرقم مرجعي إضافي: {Guid.NewGuid().ToString()[..8]}\nيرجى إفادتنا في حال الحاجة إلى معلومات إضافية.";
                    }
                }
                else if (rng.Next(0, 10) > 5)
                {
                    body += $"\n\nRef ID: {Guid.NewGuid().ToString()[..8]}\nPlease reach out if more info is needed.";
                }

                var ticket = new Ticket
                {
                    TicketNumber = $"{ticketPrefix}{sequence:D5}",
                    Title = title,
                    Description = body,
                    CategoryId = category.Id,
                    PriorityId = priorities[rng.Next(priorities.Count)],
                    StatusId = status.Id,
                    SourceId = sources[rng.Next(sources.Count)],
                    EntityId = creator.EntityId,
                    AssignedToUserId = assignees[rng.Next(assignees.Count)].Id,
                    CreatedByUserId = creator.Id,
                    CreatedAt = createdAt,
                    ProductArea = profile.ProductArea,
                    EnvironmentName = profile.EnvironmentName,
                    BrowserName = profile.BrowserName,
                    OperatingSystem = profile.OperatingSystem,
                    ExternalReferenceId = profile.ExternalReferenceId,
                    ExternalSystemName = profile.ExternalSystemName,
                    ImpactScope = profile.ImpactScope,
                    AffectedUsersCount = profile.AffectedUsersCount,
                    FirstResponseDueAt = firstResponseDue,
                    ResolutionDueAt = resolutionDue,
                    FirstRespondedAt = firstRespondedAt,
                    ResolvedAt = resolvedAt,
                    IsSlaBreached = (firstRespondedAt.HasValue && firstRespondedAt > firstResponseDue) ||
                                    (resolvedAt.HasValue && resolvedAt > resolutionDue),
                    ResolutionSummary = resolutionSummary,
                    PendingReason = status.Name == TicketStatusNames.Pending ? pendingReason : null
                };

                context.Tickets.Add(ticket);
                generated.Add((ticket, template));
            }

            await context.SaveChangesAsync();

            foreach (var item in generated)
            {
                var ticket = item.Ticket;
                var template = item.Template;

                if (rng.Next(0, 10) > 2)
                {
                    for (int j = 0; j < rng.Next(1, 6); j++)
                    {
                        context.TicketComments.Add(new TicketComment
                        {
                            TicketId = ticket.Id,
                            Content = comments[rng.Next(comments.Count)],
                            CreatedByUserId = ticket.AssignedToUserId ?? ticket.CreatedByUserId,
                            CreatedAt = ticket.CreatedAt.AddHours(rng.Next(1, 72))
                        });
                    }
                }

                if (template.Attachments.Count == 0)
                {
                    continue;
                }

                foreach (var fileName in template.Attachments)
                {
                    var sourceFile = Path.Combine(sourceAttachmentsDir, fileName);
                    if (!File.Exists(sourceFile))
                    {
                        Console.WriteLine($"⚠ Skipping missing attachment file: {fileName}");
                        continue;
                    }

                    var contentType = attachmentMeta.GetValueOrDefault(fileName, "application/octet-stream");
                    long fileSize = new FileInfo(sourceFile).Length;

                    context.TicketAttachments.Add(new TicketAttachment
                    {
                        TicketId = ticket.Id,
                        FileName = fileName,
                        FilePath = $"/uploads/seed/{fileName}",
                        ContentType = contentType,
                        FileSize = fileSize,
                        UploadedByUserId = ticket.CreatedByUserId,
                        UploadedAt = ticket.CreatedAt.AddMinutes(rng.Next(0, 120))
                    });
                }
            }

            await context.SaveChangesAsync();
            Console.WriteLine($"✅ Seeded {total} {batchLabel} tickets from Data/SeedData/seed-data.json");
        }

        private static async Task BackfillSystemThemeTokensAsync(ApplicationDbContext context)
        {
            // For each system theme, only fill the new token columns when they are still null.
            // We never overwrite anything an admin has customized.
            var defaults = new Dictionary<string, (string PrimaryContrast, string SurfaceAlt, string Elevated,
                string Success, string Warning, string Danger, string Info, string Shadow,
                string? PrimaryOverride, string? CardOverride, string? SidebarOverride, string? HeaderOverride,
                string? TextMutedOverride, string? BorderOverride, string? BgMainOverride)>(StringComparer.OrdinalIgnoreCase)
            {
                ["emerald-obsidian"] = ("#000000","#1a1a1d","#1f1f23","#34d399","#fbbf24","#f87171","#60a5fa","rgba(0, 0, 0, 0.45)",null,"#141416",null,null,"#a1a1aa","#27272a","#0a0a0a"),
                ["solar-ember"]      = ("#0b0b0b","#273345","#2c3a4f","#34d399","#fbbf24","#f87171","#60a5fa","rgba(0, 0, 0, 0.4)", null,null,"#0d1421","#0d1421",null,null,null),
                ["midnight-azure"]   = ("#ffffff","#172033","#1e293b","#34d399","#fbbf24","#f87171","#60a5fa","rgba(0, 0, 0, 0.5)", null,null,"#000814","#000814",null,null,null),
                ["cyber-neon"]       = ("#0b0b0b","#212124","#26262b","#34d399","#fbbf24","#f87171","#60a5fa","rgba(0, 0, 0, 0.5)", null,null,null,null,null,null,null),
                // Deep Forest: also overwrite the conflicting muted + lift primary
                ["deep-forest"]      = ("#022c22","#0a5e48","#0f6e54","#34d399","#fbbf24","#fca5a5","#7dd3fc","rgba(0, 0, 0, 0.45)","#10b981",null,"#011d17","#011d17","#a7d4c0",null,null),
                ["indigo-horizon"]   = ("#ffffff","#f8fafc","#ffffff","#16a34a","#d97706","#dc2626","#2563eb","rgba(15, 23, 42, 0.08)",null,null,null,"#fafbfc",null,null,null),
                // Slate Alpine: replace neutral-gray primary with sky
                ["slate-alpine"]     = ("#ffffff","#f8fafc","#ffffff","#16a34a","#d97706","#dc2626","#2563eb","rgba(15, 23, 42, 0.08)","#0ea5e9",null,null,null,null,null,null),
                // Rose Quartz: also fix conflicting reddish text
                ["rose-quartz"]      = ("#ffffff","#fef7f9","#ffffff","#16a34a","#d97706","#dc2626","#2563eb","rgba(131, 24, 67, 0.08)",null,null,null,"#fef7f9","#64748b","#fbcfe8","#fdf2f4"),
                ["emerald-light"]    = ("#ffffff","#f8fafc","#ffffff","#16a34a","#d97706","#dc2626","#2563eb","rgba(15, 23, 42, 0.08)",null,null,null,"#f8fafc",null,null,null),
            };

            // For Rose Quartz, the conflicting TextMain also needs forced replacement
            var textMainForced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rose-quartz"] = "#1f2937"
            };

            var systemThemes = await context.CustomThemes.Where(t => t.IsSystemTheme && t.SystemIdentifier != null).ToListAsync();
            var dirty = false;
            foreach (var theme in systemThemes)
            {
                if (theme.SystemIdentifier == null || !defaults.TryGetValue(theme.SystemIdentifier, out var d)) continue;

                if (string.IsNullOrWhiteSpace(theme.PrimaryContrastText)) { theme.PrimaryContrastText = d.PrimaryContrast; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.BgSurfaceAlt))        { theme.BgSurfaceAlt = d.SurfaceAlt; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.BgElevated))          { theme.BgElevated = d.Elevated; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.SuccessColor))        { theme.SuccessColor = d.Success; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.WarningColor))        { theme.WarningColor = d.Warning; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.DangerColor))         { theme.DangerColor = d.Danger; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.InfoColor))           { theme.InfoColor = d.Info; dirty = true; }
                if (string.IsNullOrWhiteSpace(theme.ShadowColor))         { theme.ShadowColor = d.Shadow; dirty = true; }

                // Conflict-fix overrides — applied unconditionally for system themes so the broken originals
                // (Slate primary gray, Deep Forest mint muted, Rose three-reds, Emerald Obsidian flat blacks)
                // get repaired. Custom themes are never touched.
                if (d.PrimaryOverride != null && theme.PrimaryColor != d.PrimaryOverride) { theme.PrimaryColor = d.PrimaryOverride; dirty = true; }
                if (d.CardOverride != null && theme.BgCard != d.CardOverride)             { theme.BgCard = d.CardOverride; dirty = true; }
                if (d.SidebarOverride != null && theme.BgSidebar != d.SidebarOverride)    { theme.BgSidebar = d.SidebarOverride; dirty = true; }
                if (d.HeaderOverride != null && theme.BgHeader != d.HeaderOverride)       { theme.BgHeader = d.HeaderOverride; dirty = true; }
                if (d.TextMutedOverride != null && theme.TextMuted != d.TextMutedOverride){ theme.TextMuted = d.TextMutedOverride; dirty = true; }
                if (d.BorderOverride != null && theme.BorderColor != d.BorderOverride)    { theme.BorderColor = d.BorderOverride; dirty = true; }
                if (d.BgMainOverride != null && theme.BgMain != d.BgMainOverride)         { theme.BgMain = d.BgMainOverride; dirty = true; }
                if (textMainForced.TryGetValue(theme.SystemIdentifier, out var tm) && theme.TextMain != tm)
                { theme.TextMain = tm; dirty = true; }
            }

            if (dirty)
            {
                await context.SaveChangesAsync();
                Console.WriteLine("Backfilled extended theme tokens for system themes.");
            }
        }

        private static async Task SeedCopilotToolsAsync(ApplicationDbContext context)
        {
            // C3 — prefer external JSON when present so the package ships with seedable tools
            // and operators can extend without recompiling. The inline fallback below ensures
            // older deployments without the JSON still get the canonical 6 tools.
            var externalUtilityTools = LoadToolsFromJsonOrFallback();

            // Inline fallback (used only when copilot-tools.json is missing/unreadable).
            static List<CopilotToolDefinition> InlineFallback() => new()
            {
                new CopilotToolDefinition
                {
                    ToolKey = "country_profile_lookup",
                    Title = "Country Profile Lookup",
                    Description = "Retrieves country facts such as capital, population, currencies, region, and time zones using the public REST Countries API.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "https://restcountries.com/v3.1/name/{query}?fields=name,capital,region,subregion,population,languages,currencies,timezones",
                    KeywordHints = "country profile,country facts,country capital,country population,country info,nation",
                    QueryExtractionHint = "Extract the country name only, such as Saudi Arabia, Japan, or Canada.",
                    ResponseFormatHint = "Summarize the top matching country result with capital, population, region, currencies, and time zones.",
                    TestPrompt = "Tell me about Saudi Arabia.",
                    IsEnabled = true,
                    SortOrder = 101
                },
                new CopilotToolDefinition
                {
                    ToolKey = "currency_country_lookup",
                    Title = "Currency Country Lookup",
                    Description = "Finds countries that use a given currency code through the REST Countries currency endpoint.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "https://restcountries.com/v3.1/currency/{query}?fields=name,capital,currencies,population,region",
                    KeywordHints = "currency country,uses currency,which countries use,eur countries,usd countries,what country uses,which country uses,country that uses,countries that use,uses the rupee,uses the euro,uses the dollar,uses the yen,uses the pound,uses the yuan,uses the rupiah,uses the peso,uses the franc,uses the kroner,uses the lira,uses the krone,which country uses the,what country uses the,countries using",
                    QueryExtractionHint = "Extract the ISO currency code only, such as USD, EUR, AED, or SAR.",
                    ResponseFormatHint = "Return the main countries that use the requested currency.",
                    TestPrompt = "Which countries use the SAR currency?",
                    IsEnabled = true,
                    SortOrder = 102
                },
                new CopilotToolDefinition
                {
                    ToolKey = "location_lookup",
                    Title = "Location Lookup",
                    Description = "Searches locations globally and returns place names, coordinates, country, population, and time zone using the Open-Meteo geocoding API.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "https://geocoding-api.open-meteo.com/v1/search?name={query}&count=5&language=en&format=json",
                    KeywordHints = "location lookup,city lookup,coordinates,timezone,place search,where is",
                    QueryExtractionHint = "Extract the place or city name only.",
                    ResponseFormatHint = "Return the best-matching locations with coordinates and time zone.",
                    TestPrompt = "Find the coordinates and timezone for Dubai.",
                    IsEnabled = true,
                    SortOrder = 103
                },
                new CopilotToolDefinition
                {
                    ToolKey = "major_fx_snapshot",
                    Title = "Major FX Snapshot",
                    Description = "Provides a quick snapshot of major exchange rates from USD to common currencies using the Frankfurter API.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "https://api.frankfurter.dev/v1/latest?base=USD&symbols=EUR,GBP,AED,SAR,JPY",
                    KeywordHints = "exchange rates,fx snapshot,currency rates,usd rates,dollar rates,convert,convert usd,convert to,usd to,to jpy,to eur,to gbp,to inr,to cad,to aud,to chf,to sgd,to hkd,to nzd,to mxn,to brl,to zar,to cny,to rub,usd to jpy,usd to eur,usd to gbp,usd jpy,usd eur,usd gbp,convert currency,currency conversion,currency exchange,convert 100,convert 50,convert 10,convert 1000,fx rate,exchange rate",
                    QueryExtractionHint = "Default to the current/latest exchange rate when no specific date is mentioned. Do NOT ask the user to disambiguate between current and historical rates - assume current. Use this tool for any USD-to-X conversion or major exchange-rate question.",
                    ResponseFormatHint = "Summarize the current/latest exchange rate from USD to the requested currency. If the user provided an amount (e.g. convert 100 USD to JPY), multiply that amount by the rate and present the result. Never ask the user for clarification about which rate to use - assume current.",
                    TestPrompt = "Show me the latest major exchange rates from USD.",
                    IsEnabled = true,
                    SortOrder = 104
                },
                new CopilotToolDefinition
                {
                    ToolKey = "public_holiday_watch",
                    Title = "Public Holiday Watch",
                    Description = "Lists the next public holidays worldwide using the Nager.Date public holiday API.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "https://date.nager.at/api/v3/NextPublicHolidaysWorldwide",
                    KeywordHints = "public holidays,next holidays,holiday calendar,world holidays,holiday watch",
                    QueryExtractionHint = "No extraction needed; use this when the user asks about upcoming public holidays worldwide.",
                    ResponseFormatHint = "Summarize the next worldwide public holidays with date and country.",
                    TestPrompt = "What are the next public holidays worldwide?",
                    IsEnabled = true,
                    SortOrder = 105
                },
                new CopilotToolDefinition
                {
                    ToolKey = "university_registry_search",
                    Title = "University Registry Search",
                    Description = "Searches universities by country using the public University Domains API.",
                    ToolType = "External",
                    CopilotMode = "ExternalUtility",
                    EndpointUrl = "http://universities.hipolabs.com/search?country={query}",
                    KeywordHints = "universities in country,colleges in country,academic registry,university search,schools by country,find universities,find universities in,universities in,university in,colleges in,college in,find colleges,list universities,show universities,university registry,academic institutions,higher education,find university,find a university",
                    QueryExtractionHint = "Extract the country name only.",
                    ResponseFormatHint = "Return the matching universities with names and website domains.",
                    TestPrompt = "Find universities in Canada.",
                    IsEnabled = true,
                    SortOrder = 106
                }
            };

            // Reads copilot-tools.json from disk and parses into CopilotToolDefinition rows.
            // Falls back to InlineFallback() when the file is missing or malformed — the package
            // still ships with a working tool set in that case. Path is resolved relative to the
            // current working directory so it works under both dotnet run and IIS hosting.
            static List<CopilotToolDefinition> LoadToolsFromJsonOrFallback()
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "Areas", "SuperAdminCopilot", "Configuration", "copilot-tools.json");
                if (!File.Exists(path)) return InlineFallback();
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array)
                        return InlineFallback();
                    var list = new List<CopilotToolDefinition>();
                    foreach (var el in toolsEl.EnumerateArray())
                    {
                        try
                        {
                            list.Add(new CopilotToolDefinition
                            {
                                ToolKey = el.GetProperty("toolKey").GetString() ?? "",
                                Title = el.GetProperty("title").GetString() ?? "",
                                Description = el.GetProperty("description").GetString() ?? "",
                                ToolType = el.TryGetProperty("toolType", out var tt) ? (tt.GetString() ?? "External") : "External",
                                CopilotMode = el.TryGetProperty("copilotMode", out var cm) ? (cm.GetString() ?? "ExternalUtility") : "ExternalUtility",
                                EndpointUrl = el.TryGetProperty("endpointUrl", out var eu) ? eu.GetString() : null,
                                KeywordHints = el.TryGetProperty("keywordHints", out var kh) ? kh.GetString() : null,
                                QueryExtractionHint = el.TryGetProperty("queryExtractionHint", out var qe) ? qe.GetString() : null,
                                ResponseFormatHint = el.TryGetProperty("responseFormatHint", out var rf) ? rf.GetString() : null,
                                TestPrompt = el.TryGetProperty("testPrompt", out var tp) ? tp.GetString() : null,
                                IsEnabled = !el.TryGetProperty("isEnabled", out var ie) || ie.GetBoolean(),
                                SortOrder = el.TryGetProperty("sortOrder", out var so) ? so.GetInt32() : 0
                            });
                        }
                        catch
                        {
                            // Skip individual malformed entries — keep the rest.
                        }
                    }
                    return list.Count > 0 ? list : InlineFallback();
                }
                catch
                {
                    return InlineFallback();
                }
            }

            // **UPSERT semantics** (changed 2026-05-19 — see commit history): for each seeded tool
            // we INSERT if absent and UPDATE selectively otherwise. We intentionally preserve any
            // admin-tuned fields (KeywordHints, QueryExtractionHint, ResponseFormatHint, IsEnabled,
            // SortOrder) when a row already exists — only overwrite when the row is genuinely new.
            //
            // <para><b>Why this matters:</b> the previous implementation did a wipe-and-reseed on
            // every startup, which silently undid any admin/UI customization (e.g. expanded keyword
            // hints, disabled tools). That bug masked a 4-iteration test-loop diagnostic for hours
            // — every restart reverted the test fix. If you genuinely need to reset to defaults,
            // do it via a one-off SQL `TRUNCATE` or a dedicated admin endpoint, NOT by tweaking
            // this seeder.</para>
            //
            // <para>Endpoint URL, Title, Description, ToolType, CopilotMode, TestPrompt are
            // considered "structural" — overwritten on every seed to match the deployment's
            // current code-level definition. The "tunable" fields above are preserved.</para>
            var existingByKey = (await context.CopilotToolDefinitions.ToListAsync())
                .ToDictionary(t => t.ToolKey, StringComparer.OrdinalIgnoreCase);

            foreach (var seedTool in externalUtilityTools)
            {
                if (existingByKey.TryGetValue(seedTool.ToolKey, out var existing))
                {
                    // Refresh structural fields only — preserve admin-tuned fields.
                    existing.Title = seedTool.Title;
                    existing.Description = seedTool.Description;
                    existing.ToolType = seedTool.ToolType;
                    existing.CopilotMode = seedTool.CopilotMode;
                    existing.EndpointUrl = seedTool.EndpointUrl;
                    existing.TestPrompt = seedTool.TestPrompt;
                    // KeywordHints / QueryExtractionHint / ResponseFormatHint / IsEnabled / SortOrder
                    // intentionally NOT touched — preserve admin tuning.
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    context.CopilotToolDefinitions.Add(seedTool);
                }
            }
            await context.SaveChangesAsync();
        }
    }
}
