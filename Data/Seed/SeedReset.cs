using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Data.Seed;

/// <summary>
/// Wipes every seed-managed table so the three seeders (Core / ServiceOps / Phase06)
/// re-run from scratch on the next startup — used when the
/// <c>Seed:ForceReseed</c> config flag is set. Identity, lookups populated by
/// migrations, copilot trace history, and __EFMigrationsHistory are preserved
/// so the database structure and the admin login survive the wipe.
/// </summary>
internal static class SeedReset
{
    /// <summary>
    /// Order matters — children before parents — because we use plain DELETE
    /// rather than DISABLE/RE-ENABLE the FK constraints.
    /// </summary>
    private static readonly string[] WipeOrder =
    {
        // Tier 1: rows that depend on Tickets / Bills / Outages / Assets / Customers
        "TicketAttachments",
        "TicketComments",
        "TicketHistories",
        "CsatResponses",
        "WorkOrders",
        "CallLogs",
        "OutageNotifications",
        "Subsidies",
        "Payments",
        "MeterReadings",
        "MaintenanceSchedules",
        "ServiceAccounts",
        "Notifications",

        // Tier 2: rows under Customers / Departments / Assets
        "Tickets",
        "Bills",
        "Outages",
        "ServicePoints",
        "Assets",
        "Technicians",

        // Tier 3: top-level seeded entities (TariffTiers BEFORE Tariffs — FK)
        "TariffTiers",
        "Tariffs",
        "SlaPolicies",
        "Customers",
    };

    public static async Task WipeAsync(ApplicationDbContext context, CancellationToken ct = default)
    {
        Console.WriteLine("⚠ Seed:ForceReseed=true — wiping seed-managed tables…");

        foreach (var table in WipeOrder)
        {
            var rows = await context.Database.ExecuteSqlRawAsync($"DELETE FROM [{table}]", ct);
            Console.WriteLine($"   - {table}: removed {rows} rows");
        }

        // Reset IDENTITY so re-seeded rows start from 1 again (otherwise IDs keep
        // climbing across reseeds, which is fine functionally but noisy in logs).
        foreach (var table in WipeOrder)
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    $"IF OBJECT_ID('[{table}]') IS NOT NULL AND (SELECT IDENT_CURRENT('[{table}]')) IS NOT NULL DBCC CHECKIDENT('[{table}]', RESEED, 0)",
                    ct);
            }
            catch
            {
                // Some tables don't have an identity column — DBCC will throw; ignore.
            }
        }

        Console.WriteLine("✅ Wipe complete — seeders will repopulate on startup.");
    }
}
