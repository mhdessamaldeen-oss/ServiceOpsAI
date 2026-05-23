using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Models;

namespace ServiceOpsAI.Data.Seed;

/// <summary>
/// Seeds the utility-domain data (Departments, Customers, Bills, Tickets) with Syrian-flavored
/// names, geography, and 24 months of bill history. Layered with scripted story patterns so the
/// SuperAdminCopilot has discoverable patterns to reason over (outage clusters, bill anomalies,
/// churn signals, VIP customers, an underperforming department).
///
/// Idempotent: detects existing Customers and no-ops. Deterministic: fixed Random seed.
/// Country + Region data is seeded by the migration (Phase 02) — this class assumes those exist.
/// </summary>
public static class ServiceOpsSeeder
{
    private const int RandomSeed = 42;
    private const int CustomerCount = 200;
    private const int BillHistoryMonths = 24;

    // Reference date: bills go backwards from the start of this month.
    private static readonly DateTime SeedReferenceDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task SeedAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (await db.Customers.AnyAsync(ct)) return; // idempotent
        if (!await db.Regions.AnyAsync(ct))
            throw new InvalidOperationException("Region data is missing. Apply the AddCountryAndRegion migration first.");

        var rng = new Random(RandomSeed);

        await SeedTicketCategoriesAsync(db, ct);
        var departments = await SeedDepartmentsAsync(db, ct);
        var customers = await SeedCustomersAsync(db, rng, ct);
        var bills = await SeedBillsAsync(db, rng, customers, departments, ct);
        await SeedTicketsAsync(db, rng, customers, departments, bills, ct);
    }

    // ─── TicketCategory: 10 utility-realistic bilingual categories (upsert by name) ──────────────
    private static async Task SeedTicketCategoriesAsync(ApplicationDbContext db, CancellationToken ct)
    {
        // Upsert by Name match so existing tickets that reference older categories keep their FK,
        // and we don't trigger an FK violation by deleting categories in use.
        var desired = new (string Name, string NameAr)[]
        {
            ("Internet outage",                 "انقطاع الإنترنت"),
            ("Internet slow speed",             "بطء سرعة الإنترنت"),
            ("Electricity outage",              "انقطاع الكهرباء"),
            ("Water cut",                       "انقطاع المياه"),
            ("Gas service issue",               "مشكلة في خدمة الغاز"),
            ("Billing dispute - amount",        "اعتراض على قيمة الفاتورة"),
            ("Billing dispute - meter reading", "اعتراض على قراءة العداد"),
            ("New service request",             "طلب خدمة جديدة"),
            ("Service disconnection issue",     "مشكلة في فصل الخدمة"),
            ("Technician visit needed",         "طلب زيارة فني"),
        };

        var existing = await db.TicketCategories.ToListAsync(ct);
        foreach (var (name, nameAr) in desired)
        {
            var match = existing.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                db.TicketCategories.Add(new TicketCategory { Name = name, NameAr = nameAr, IsActive = true });
            }
            else if (match.NameAr != nameAr)
            {
                match.NameAr = nameAr;
                db.TicketCategories.Update(match);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ─── Departments: 14 governorates × 4 service types = 56 departments ────────────────────────
    private static async Task<List<Department>> SeedDepartmentsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        // Load governorates (RegionType=Governorate) — they have ParentRegionId = null.
        var governorates = await db.Regions
            .Where(r => r.RegionType == RegionType.Governorate)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var depts = new List<Department>();
        foreach (var gov in governorates)
        {
            foreach (var svc in new[] { ServiceType.Electricity, ServiceType.Internet, ServiceType.Water, ServiceType.Gas })
            {
                depts.Add(new Department
                {
                    NameEn = $"{gov.NameEn} {ServiceLabelEn(svc)} Department",
                    NameAr = $"إدارة {ServiceLabelAr(svc)} {gov.NameAr}",
                    ServiceType = svc,
                    RegionId = gov.Id,
                    ContactPhone = $"+963 {rng_phone(gov.Id, (int)svc)}",
                    ContactEmail = $"{svc.ToString().ToLower()}.{TransliterateGov(gov.NameEn)}@serviceops.sy",
                    IsActive = true,
                });
            }
        }
        db.Departments.AddRange(depts);
        await db.SaveChangesAsync(ct);
        return depts;
    }

    private static string ServiceLabelEn(ServiceType svc) => svc switch
    {
        ServiceType.Electricity => "Electricity",
        ServiceType.Internet    => "Internet",
        ServiceType.Water       => "Water",
        ServiceType.Gas         => "Gas",
        _ => svc.ToString(),
    };

    private static string ServiceLabelAr(ServiceType svc) => svc switch
    {
        ServiceType.Electricity => "كهرباء",
        ServiceType.Internet    => "إنترنت",
        ServiceType.Water       => "مياه",
        ServiceType.Gas         => "غاز",
        _ => svc.ToString(),
    };

    private static string TransliterateGov(string nameEn) =>
        nameEn.ToLower().Replace(" ", "").Replace("-", "");

    private static string rng_phone(int govId, int svc)
    {
        // Department contact: deterministic synthetic number per (gov, svc) pair.
        var n = (govId * 100 + svc * 7) % 10000;
        return $"11 {n:0000} 100";
    }

    // ─── Customers: 200 distributed across districts, weighted by governorate population ─────────
    private static async Task<List<Customer>> SeedCustomersAsync(ApplicationDbContext db, Random rng, CancellationToken ct)
    {
        // Weighted distribution per Phase 03 plan (sums to 200).
        var distribution = new Dictionary<string, int>
        {
            ["Damascus"]    = 30,
            ["Rif Dimashq"] = 25,
            ["Aleppo"]      = 35,
            ["Homs"]        = 18,
            ["Hama"]        = 14,
            ["Lattakia"]    = 12,
            ["Tartus"]      = 8,
            ["Idlib"]       = 10,
            ["Daraa"]       = 8,
            ["As-Suwayda"]  = 6,
            ["Quneitra"]    = 4,
            ["Deir ez-Zor"] = 10,
            ["Raqqa"]       = 8,
            ["Al-Hasakah"]  = 12,
        };

        // Load all districts grouped by parent governorate name.
        var allRegions = await db.Regions.ToListAsync(ct);
        var govByName  = allRegions.Where(r => r.RegionType == RegionType.Governorate)
                                   .ToDictionary(r => r.NameEn, r => r);
        var districtsByGovId = allRegions.Where(r => r.RegionType == RegionType.District)
                                         .GroupBy(r => r.ParentRegionId!.Value)
                                         .ToDictionary(g => g.Key, g => g.ToList());

        var customers = new List<Customer>();
        var usedNationalIds = new HashSet<string>();
        var signupCutoff = SeedReferenceDate.AddYears(-3);

        foreach (var (govName, count) in distribution)
        {
            if (!govByName.TryGetValue(govName, out var gov)) continue;
            var districts = districtsByGovId.TryGetValue(gov.Id, out var ds) ? ds : new List<Region> { gov };

            for (int i = 0; i < count; i++)
            {
                var gender = rng.Next(2) == 0 ? Gender.Male : Gender.Female;
                var first = PickName(rng, gender);
                var surname = PickSurname(rng);
                var district = districts[rng.Next(districts.Count)];

                string nationalId;
                do { nationalId = GenerateNationalId(rng); } while (!usedNationalIds.Add(nationalId));

                var hasEmail = rng.NextDouble() < 0.6;
                var fullEn = $"{first.En} {surname.En}";
                var fullAr = $"{first.Ar} {surname.Ar}";
                var providers = new[] { "mail", "syriatel", "mtn", "connect" };
                var email = hasEmail
                    ? $"{first.En.ToLower()}.{surname.En.ToLower().Replace("al-", "").Replace(" ", "")}{rng.Next(10, 99)}@{providers[rng.Next(providers.Length)]}.sy"
                    : null;

                var statusRoll = rng.NextDouble();
                var status = statusRoll < 0.05 ? CustomerStatus.Suspended
                          : statusRoll < 0.10 ? CustomerStatus.Churned
                          : CustomerStatus.Active;
                var signupAt = signupCutoff.AddDays(rng.Next(0, 365 * 2));
                DateTime? churnedAt = status == CustomerStatus.Churned
                    ? SeedReferenceDate.AddDays(-rng.Next(1, 180))
                    : null;

                customers.Add(new Customer
                {
                    FullNameEn   = fullEn,
                    FullNameAr   = fullAr,
                    NationalId   = nationalId,
                    Email        = email,
                    Phone        = GeneratePhone(rng),
                    RegionId     = district.Id,
                    AddressLineEn = $"{district.NameEn}, Building {rng.Next(1, 99)}",
                    AddressLineAr = $"{district.NameAr}، بناء {rng.Next(1, 99)}",
                    Status       = status,
                    SignupAt     = signupAt,
                    ChurnedAt    = churnedAt,
                });
            }
        }

        db.Customers.AddRange(customers);
        await db.SaveChangesAsync(ct);
        return customers;
    }

    private static string GeneratePhone(Random rng) =>
        $"+963 9{rng.Next(10, 100):00} {rng.Next(100, 1000):000} {rng.Next(100, 1000):000}";

    private static string GenerateNationalId(Random rng)
    {
        // Fake but format-realistic: 11 digits, first digit 1 or 2.
        var first = rng.Next(2) + 1;
        var rest = rng.NextInt64(1_000_000_000L, 10_000_000_000L);
        return $"{first}{rest}";
    }

    // ─── Bills: 24 months of history per customer-service relationship ──────────────────────────
    private static async Task<List<Bill>> SeedBillsAsync(
        ApplicationDbContext db, Random rng,
        List<Customer> customers, List<Department> departments,
        CancellationToken ct)
    {
        // Service subscription probabilities per customer
        var subscribesElectricity = 0.90;
        var subscribesInternet    = 0.75;
        var subscribesWater       = 0.60;
        var subscribesGas         = 0.30;

        // Department lookup by (RegionId, ServiceType)
        var deptByGovSvc = departments.ToDictionary(d => (d.RegionId!.Value, d.ServiceType));

        // Helper: load customer governorate (district -> parent governorate)
        var allRegions = await db.Regions.ToListAsync(ct);
        var govById = allRegions.Where(r => r.RegionType == RegionType.Governorate).ToDictionary(r => r.Id, r => r);
        var districtToGov = allRegions.Where(r => r.RegionType == RegionType.District)
                                      .ToDictionary(r => r.Id, r => r.ParentRegionId!.Value);

        var bills = new List<Bill>();
        var billNumberSeq = new Dictionary<(int year, int month, ServiceType svc), int>();

        // Story 7: pick one "bad department" for elevated overdue rates.
        var badDeptId = departments[rng.Next(departments.Count)].Id;
        // Story 8: pick one "star department" for excellent metrics (must differ from bad).
        Department starDept;
        do { starDept = departments[rng.Next(departments.Count)]; } while (starDept.Id == badDeptId);
        var starDeptId = starDept.Id;

        // Story 3: pick 5 Homs electricity customers for the bill-anomaly story (Nov 2025 - Jan 2026 spike).
        var homsGov = govById.Values.FirstOrDefault(g => g.NameEn == "Homs");
        var homsElectricityCustomers = new HashSet<int>();
        if (homsGov is not null)
        {
            var candidates = customers
                .Where(c => c.RegionId.HasValue && districtToGov.TryGetValue(c.RegionId.Value, out var gov) && gov == homsGov.Id)
                .OrderBy(c => c.NationalId) // deterministic order
                .Take(5)
                .Select(c => c.Id)
                .ToList();
            foreach (var id in candidates) homsElectricityCustomers.Add(id);
        }

        foreach (var customer in customers)
        {
            // Resolve customer's governorate (each customer's RegionId is a district).
            if (!customer.RegionId.HasValue) continue;
            if (!districtToGov.TryGetValue(customer.RegionId.Value, out var govId)) continue;

            var services = new List<ServiceType>();
            if (rng.NextDouble() < subscribesElectricity) services.Add(ServiceType.Electricity);
            if (rng.NextDouble() < subscribesInternet)    services.Add(ServiceType.Internet);
            if (rng.NextDouble() < subscribesWater)       services.Add(ServiceType.Water);
            if (rng.NextDouble() < subscribesGas)         services.Add(ServiceType.Gas);

            foreach (var svc in services)
            {
                if (!deptByGovSvc.TryGetValue((govId, svc), out var dept)) continue;
                var isBadDept = dept.Id == badDeptId;
                var isStarDept = dept.Id == starDeptId;

                // Generate 24 months of bills, ending at SeedReferenceDate (exclusive).
                for (int monthsAgo = BillHistoryMonths; monthsAgo >= 1; monthsAgo--)
                {
                    var periodStart = SeedReferenceDate.AddMonths(-monthsAgo);
                    var periodEnd   = periodStart.AddMonths(1).AddDays(-1);
                    var month = periodStart.Month;

                    var seasonal = SeasonalMultiplier(svc, month);
                    var (baseAmt, usageAmt, qty, unit) = GenerateAmounts(rng, svc, seasonal);

                    // Story 3: Homs electricity customers — spike 2x in Nov 2025, Dec 2025, Jan 2026.
                    var isHomsSpikeBill = svc == ServiceType.Electricity
                        && homsElectricityCustomers.Contains(customer.Id)
                        && ((periodStart.Year == 2025 && (periodStart.Month == 11 || periodStart.Month == 12))
                            || (periodStart.Year == 2026 && periodStart.Month == 1));
                    if (isHomsSpikeBill) usageAmt *= 2.0m;

                    var taxes = Math.Round((baseAmt + usageAmt) * 0.11m / 100m) * 100m;
                    var total = baseAmt + usageAmt + taxes;

                    var seqKey = (periodStart.Year, periodStart.Month, svc);
                    billNumberSeq.TryGetValue(seqKey, out var seq);
                    billNumberSeq[seqKey] = ++seq;

                    var billNumber = $"{ServicePrefix(svc)}-{periodStart:yyyy-MM}-{seq:000000}";

                    var dueDate = periodEnd.AddDays(15);

                    // Status distribution + story modifications
                    var statusRoll = rng.NextDouble();
                    var overdueThreshold = isBadDept ? 0.30 : (isStarDept ? 0.04 : 0.12);
                    var paidThreshold    = isStarDept ? 0.95 : 0.70;

                    BillStatus status;
                    DateTime? paidAt = null;
                    string? paymentMethod = null;
                    if (monthsAgo == 1 && statusRoll < 0.15)
                    {
                        // Current month — some still in "Issued" state, not yet paid
                        status = BillStatus.Issued;
                    }
                    else if (statusRoll < overdueThreshold && monthsAgo <= 6)
                    {
                        status = BillStatus.Overdue;
                    }
                    else if (statusRoll < paidThreshold || monthsAgo > 6)
                    {
                        status = BillStatus.Paid;
                        paidAt = periodEnd.AddDays(rng.Next(-5, 14));
                        paymentMethod = new[] { "Card", "Cash", "Transfer", "Wallet" }[rng.Next(4)];
                    }
                    else
                    {
                        status = BillStatus.Issued;
                    }

                    bills.Add(new Bill
                    {
                        BillNumber     = billNumber,
                        CustomerId     = customer.Id,
                        DepartmentId   = dept.Id,
                        ServiceType    = svc,
                        PeriodStart    = periodStart,
                        PeriodEnd      = periodEnd,
                        BaseAmount     = baseAmt,
                        UsageAmount    = usageAmt,
                        Taxes          = taxes,
                        TotalAmount    = total,
                        UsageQuantity  = qty,
                        UsageUnit      = unit,
                        Status         = status,
                        IssuedAt       = periodEnd,
                        DueDate        = dueDate,
                        PaidAt         = paidAt,
                        PaymentMethod  = paymentMethod,
                    });
                }
            }
        }

        // Save in batches to avoid huge SaveChanges payloads.
        const int batchSize = 1000;
        for (int i = 0; i < bills.Count; i += batchSize)
        {
            db.Bills.AddRange(bills.Skip(i).Take(batchSize));
            await db.SaveChangesAsync(ct);
        }
        return bills;
    }

    private static double SeasonalMultiplier(ServiceType svc, int month) => svc switch
    {
        ServiceType.Electricity => (month >= 6 && month <= 9) ? 1.30 : (month == 12 || month <= 2) ? 1.15 : 1.0,
        ServiceType.Water       => (month >= 6 && month <= 9) ? 1.20 : 1.0,
        ServiceType.Gas         => (month == 12 || month <= 2) ? 1.40 : 1.0,
        _ => 1.0,
    };

    private static (decimal baseAmt, decimal usageAmt, decimal qty, string unit) GenerateAmounts(Random rng, ServiceType svc, double seasonal)
    {
        return svc switch
        {
            ServiceType.Electricity => (
                2_000m,
                Round100((decimal)(rng.Next(3_000, 48_000) * seasonal)),
                (decimal)(rng.Next(100, 800) * seasonal),
                "kWh"
            ),
            ServiceType.Internet => (
                25_000m,
                Round100((decimal)rng.Next(5_000, 55_000)),
                rng.Next(50, 500),
                "GB"
            ),
            ServiceType.Water => (
                1_500m,
                Round100((decimal)(rng.Next(1_500, 13_500) * seasonal)),
                (decimal)(rng.Next(10, 50) * seasonal),
                "m³"
            ),
            ServiceType.Gas => (
                5_000m,
                Round100((decimal)(rng.Next(5_000, 25_000) * seasonal)),
                (decimal)(rng.Next(1, 8) * seasonal),
                "cyl"
            ),
            _ => (0m, 0m, 0m, "")
        };
    }

    private static decimal Round100(decimal amount) => Math.Round(amount / 100m) * 100m;

    private static string ServicePrefix(ServiceType svc) => svc switch
    {
        ServiceType.Electricity => "ELEC",
        ServiceType.Internet    => "INT",
        ServiceType.Water       => "WTR",
        ServiceType.Gas         => "GAS",
        _ => "OTH",
    };

    // ─── Tickets: ~80 base + story patterns ─────────────────────────────────────────────────────
    private static async Task SeedTicketsAsync(
        ApplicationDbContext db, Random rng,
        List<Customer> customers, List<Department> departments, List<Bill> bills,
        CancellationToken ct)
    {
        // Required lookups for FK fields
        var categories = await db.TicketCategories.ToListAsync(ct);
        var priorities = await db.TicketPriorities.ToListAsync(ct);
        var statuses   = await db.TicketStatuses.ToListAsync(ct);
        var sources    = await db.TicketSources.ToListAsync(ct);

        if (categories.Count == 0 || priorities.Count == 0 || statuses.Count == 0 || sources.Count == 0)
            return; // Existing TicketPriority/Status/Source seed hasn't run yet — skip.

        // Need an internal staff user as CreatedByUserId.
        var staffUserId = await db.Users.Select(u => u.Id).FirstOrDefaultAsync(ct);
        if (staffUserId is null) return;

        var allRegions = await db.Regions.ToListAsync(ct);
        var districtToGov = allRegions.Where(r => r.RegionType == RegionType.District)
                                      .ToDictionary(r => r.Id, r => r.ParentRegionId!.Value);
        var deptByGovSvc = departments.ToDictionary(d => (d.RegionId!.Value, d.ServiceType));

        var tickets = new List<Ticket>();
        int ticketSeq = 1;

        // Story 1: Aleppo internet outage on 2026-03-14 — 20 tickets.
        var aleppoInternetDept = departments.FirstOrDefault(d => d.ServiceType == ServiceType.Internet
            && allRegions.First(r => r.Id == d.RegionId).NameEn == "Aleppo");
        var aleppoCustomers = customers
            .Where(c => c.RegionId.HasValue && districtToGov.TryGetValue(c.RegionId.Value, out var g)
                && allRegions.First(r => r.Id == g).NameEn == "Aleppo")
            .OrderBy(c => c.NationalId)
            .Take(20)
            .ToList();

        if (aleppoInternetDept is not null)
        {
            var outageStart = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);
            var outageEnd   = new DateTime(2026, 3, 14, 18, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < aleppoCustomers.Count; i++)
            {
                var c = aleppoCustomers[i];
                var resolved = i < 16; // 80% auto-resolve when outage clears
                var status = resolved ? FindStatus(statuses, "Resolved", "Closed", "Completed") : FindStatus(statuses, "Open", "InProgress");
                tickets.Add(new Ticket
                {
                    TicketNumber          = $"TKT-{ticketSeq++:00000}",
                    Title                 = $"No internet at {c.AddressLineEn}",
                    Description           = "Internet completely down since this morning. Please restore service urgently.",
                    CategoryId            = FindCategory(categories, "Internet outage").Id,
                    PriorityId            = priorities[Math.Min(2, priorities.Count - 1)].Id,
                    StatusId              = status.Id,
                    SourceId              = sources[0].Id,
                    DepartmentId          = aleppoInternetDept.Id,
                    CreatedByUserId       = staffUserId,
                    CreatedAt             = outageStart.AddMinutes(rng.Next(0, 360)),
                    CustomerId            = c.Id,
                    ComplaintType         = ComplaintType.ServiceDown,
                    ResolvedAt            = resolved ? outageEnd.AddMinutes(rng.Next(0, 60)) : null,
                });
            }
        }

        // Story 3 follow-up: Homs electricity bill-anomaly customers — 3 of 5 file BillingDispute tickets.
        var homsGovId = allRegions.FirstOrDefault(r => r.NameEn == "Homs")?.Id;
        if (homsGovId.HasValue)
        {
            var homsElectricityBills = bills
                .Where(b => b.ServiceType == ServiceType.Electricity
                    && b.UsageAmount > 30_000m // the spiked ones
                    && customers.Any(c => c.Id == b.CustomerId
                        && c.RegionId.HasValue && districtToGov.TryGetValue(c.RegionId.Value, out var g) && g == homsGovId))
                .Take(3)
                .ToList();

            var homsElectricityDept = deptByGovSvc[(homsGovId.Value, ServiceType.Electricity)];
            foreach (var bill in homsElectricityBills)
            {
                tickets.Add(new Ticket
                {
                    TicketNumber    = $"TKT-{ticketSeq++:00000}",
                    Title           = $"Bill for {bill.PeriodStart:yyyy-MM} is much higher than usual",
                    Description     = $"My electricity bill went up to {bill.TotalAmount:N0} SYP from a normal {bill.TotalAmount / 2:N0}. Please review.",
                    CategoryId      = FindCategory(categories, "Billing dispute - amount").Id,
                    PriorityId      = priorities[Math.Min(1, priorities.Count - 1)].Id,
                    StatusId        = FindStatus(statuses, "InProgress", "Open").Id,
                    SourceId        = sources[0].Id,
                    DepartmentId    = homsElectricityDept.Id,
                    CreatedByUserId = staffUserId,
                    CreatedAt       = bill.PeriodEnd.AddDays(rng.Next(3, 20)),
                    CustomerId      = bill.CustomerId,
                    RelatedBillId   = bill.Id,
                    ComplaintType   = ComplaintType.BillingDispute,
                });
            }
        }

        // Base random tickets — ~60 more, mixed across customers and complaint types.
        var complaintTypes = Enum.GetValues<ComplaintType>();
        for (int i = 0; i < 60; i++)
        {
            var c = customers[rng.Next(customers.Count)];
            if (!c.RegionId.HasValue) continue;
            if (!districtToGov.TryGetValue(c.RegionId.Value, out var govId)) continue;

            var svc = (ServiceType)(rng.Next(4) + 1);
            if (!deptByGovSvc.TryGetValue((govId, svc), out var dept)) continue;

            var ct2 = complaintTypes[rng.Next(complaintTypes.Length)];
            var statusName = rng.NextDouble() < 0.60 ? "Resolved" : (rng.NextDouble() < 0.62 ? "InProgress" : "Open");
            var status = FindStatus(statuses, statusName, "Open");
            var category = FindCategoryForComplaint(categories, ct2, svc);

            // Pick a recent bill of this customer/service for BillingDispute tickets.
            int? relatedBillId = null;
            if (ct2 == ComplaintType.BillingDispute)
            {
                var candidate = bills.Where(b => b.CustomerId == c.Id && b.ServiceType == svc).LastOrDefault();
                relatedBillId = candidate?.Id;
            }

            tickets.Add(new Ticket
            {
                TicketNumber    = $"TKT-{ticketSeq++:00000}",
                Title           = TitleForComplaint(ct2, svc, c.AddressLineEn ?? "the area"),
                Description     = DescriptionForComplaint(ct2, svc),
                CategoryId      = category.Id,
                PriorityId      = priorities[rng.Next(priorities.Count)].Id,
                StatusId        = status.Id,
                SourceId        = sources[rng.Next(sources.Count)].Id,
                DepartmentId    = dept.Id,
                CreatedByUserId = staffUserId,
                CreatedAt       = SeedReferenceDate.AddDays(-rng.Next(1, 180)),
                CustomerId      = c.Id,
                RelatedBillId   = relatedBillId,
                ComplaintType   = ct2,
                ResolvedAt      = status.IsClosedState ? SeedReferenceDate.AddDays(-rng.Next(1, 90)) : null,
            });
        }

        // Save tickets in batches.
        const int batchSize = 200;
        for (int i = 0; i < tickets.Count; i += batchSize)
        {
            db.Tickets.AddRange(tickets.Skip(i).Take(batchSize));
            await db.SaveChangesAsync(ct);
        }
    }

    private static TicketStatus FindStatus(List<TicketStatus> all, params string[] preferred)
    {
        foreach (var p in preferred)
        {
            var s = all.FirstOrDefault(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (s is not null) return s;
        }
        return all.First();
    }

    private static TicketCategory FindCategory(List<TicketCategory> all, string name) =>
        all.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? all.First();

    private static TicketCategory FindCategoryForComplaint(List<TicketCategory> all, ComplaintType ct, ServiceType svc) =>
        ct switch
        {
            ComplaintType.ServiceDown when svc == ServiceType.Internet    => FindCategory(all, "Internet outage"),
            ComplaintType.ServiceDown when svc == ServiceType.Electricity => FindCategory(all, "Electricity outage"),
            ComplaintType.ServiceDown when svc == ServiceType.Water       => FindCategory(all, "Water cut"),
            ComplaintType.ServiceDown when svc == ServiceType.Gas         => FindCategory(all, "Gas service issue"),
            ComplaintType.ServiceDegraded when svc == ServiceType.Internet => FindCategory(all, "Internet slow speed"),
            ComplaintType.BillingDispute  => FindCategory(all, "Billing dispute - amount"),
            ComplaintType.MeterIssue      => FindCategory(all, "Billing dispute - meter reading"),
            ComplaintType.NewConnection   => FindCategory(all, "New service request"),
            ComplaintType.Disconnection   => FindCategory(all, "Service disconnection issue"),
            _                              => FindCategory(all, "Technician visit needed"),
        };

    private static string TitleForComplaint(ComplaintType ct, ServiceType svc, string addr) => ct switch
    {
        ComplaintType.ServiceDown        => $"{ServiceLabelEn(svc)} service is completely down at {addr}",
        ComplaintType.ServiceDegraded    => $"{ServiceLabelEn(svc)} service is very slow",
        ComplaintType.BillingDispute     => "Bill amount is incorrect — please review",
        ComplaintType.MeterIssue         => "Meter reading appears wrong",
        ComplaintType.NewConnection      => $"Request new {ServiceLabelEn(svc)} connection",
        ComplaintType.Disconnection      => $"{ServiceLabelEn(svc)} was disconnected without notice",
        _                                 => $"{ServiceLabelEn(svc)} issue",
    };

    private static string DescriptionForComplaint(ComplaintType ct, ServiceType svc) => ct switch
    {
        ComplaintType.ServiceDown        => $"The {ServiceLabelEn(svc).ToLower()} service stopped working. Need urgent restoration.",
        ComplaintType.ServiceDegraded    => $"{ServiceLabelEn(svc)} is functional but performance is unacceptably degraded.",
        ComplaintType.BillingDispute     => "My recent bill is significantly higher than my usual amount. Please review the calculation.",
        ComplaintType.MeterIssue         => "I believe the meter reading on my last bill does not match my actual usage. Please send a technician to verify.",
        ComplaintType.NewConnection      => $"I would like to subscribe to {ServiceLabelEn(svc).ToLower()} service at my address.",
        ComplaintType.Disconnection      => $"{ServiceLabelEn(svc)} was cut off without prior notification. Please restore.",
        _                                 => $"I need assistance with my {ServiceLabelEn(svc).ToLower()} service.",
    };

    // ─── Name pools ────────────────────────────────────────────────────────────────────────────
    private enum Gender { Male, Female }

    private static readonly (string En, string Ar)[] MaleFirstNames =
    {
        ("Ahmad", "أحمد"), ("Mohammed", "محمد"), ("Omar", "عمر"), ("Yusuf", "يوسف"),
        ("Khaled", "خالد"), ("Bashar", "بشار"), ("Ziad", "زياد"), ("Hisham", "هشام"),
        ("Mahmoud", "محمود"), ("Samer", "سامر"), ("Tarek", "طارق"), ("Hadi", "هادي"),
        ("Walid", "وليد"), ("Fadi", "فادي"), ("Rami", "رامي"), ("Karim", "كريم"),
        ("Anas", "أنس"), ("Adnan", "عدنان"), ("Bassam", "بسام"), ("Faisal", "فيصل"),
    };

    private static readonly (string En, string Ar)[] FemaleFirstNames =
    {
        ("Layla", "ليلى"), ("Fatima", "فاطمة"), ("Mariam", "مريم"), ("Hala", "هالة"),
        ("Maya", "مايا"), ("Rana", "رنا"), ("Nour", "نور"), ("Lina", "لينا"),
        ("Yara", "يارا"), ("Dima", "ديمة"), ("Sara", "سارة"), ("Reem", "ريم"),
        ("Hanan", "حنان"), ("Salma", "سلمى"), ("Rasha", "رشا"), ("Aya", "آية"),
        ("Sawsan", "سوسن"), ("Hiba", "هبة"), ("Marwa", "مروة"), ("Ghada", "غادة"),
    };

    private static readonly (string En, string Ar)[] Surnames =
    {
        ("Al-Hamwi", "الحموي"), ("Al-Shami", "الشامي"), ("Al-Khoury", "الخوري"),
        ("Al-Halabi", "الحلبي"), ("Al-Dimashqi", "الدمشقي"), ("Saleh", "صالح"),
        ("Nasser", "نصر"), ("Haddad", "حداد"), ("Khalil", "خليل"), ("Abboud", "عبود"),
        ("Mansour", "منصور"), ("Sayegh", "صايغ"), ("Aoun", "عون"), ("Daher", "ضاهر"),
        ("Najjar", "نجار"), ("Issa", "عيسى"), ("Ibrahim", "إبراهيم"), ("Hijazi", "حجازي"),
        ("Mardini", "مارديني"), ("Tlass", "طلاس"), ("Atassi", "أتاسي"), ("Quwatli", "قوتلي"),
        ("Houri", "حوري"), ("Barakat", "بركات"), ("Rifai", "رفاعي"), ("Kaylani", "كيلاني"),
        ("Sheikh", "شيخ"), ("Jabri", "جابري"), ("Kayyali", "كيالي"), ("Akkad", "عقاد"),
    };

    private static (string En, string Ar) PickName(Random rng, Gender gender)
    {
        var pool = gender == Gender.Male ? MaleFirstNames : FemaleFirstNames;
        return pool[rng.Next(pool.Length)];
    }

    private static (string En, string Ar) PickSurname(Random rng) => Surnames[rng.Next(Surnames.Length)];
}
