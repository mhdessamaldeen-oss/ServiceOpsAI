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
/// Lookup tables (ServiceType / ComplaintType / ResolutionType / Region / Country / TicketCategory)
/// are populated by their migrations; this seeder reads them by Code at runtime.
/// </summary>
public static class ServiceOpsSeeder
{
    private const int RandomSeed = 42;
    private const int CustomerCount = 200;
    private const int BillHistoryMonths = 24;

    private static readonly DateTime SeedReferenceDate = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task SeedAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (await db.Customers.AnyAsync(ct)) return; // idempotent
        if (!await db.Regions.AnyAsync(ct))
            throw new InvalidOperationException("Region data is missing. Apply the AddCountryAndRegion migration first.");
        if (!await db.ServiceTypes.AnyAsync(ct))
            throw new InvalidOperationException("ServiceType lookup is missing. Apply the PromoteEnumsToLookupTables migration first.");

        var rng = new Random(RandomSeed);

        // Load lookups once (Code -> row) so the seeder can build FKs without hard-coding ids.
        var svcByCode = await db.ServiceTypes.AsNoTracking().ToDictionaryAsync(s => s.Code, ct);
        var complaintByCode = await db.ComplaintTypes.AsNoTracking().ToDictionaryAsync(c => c.Code, ct);
        var resolutionByCode = await db.ResolutionTypes.AsNoTracking().ToDictionaryAsync(r => r.Code, ct);

        await SeedTicketCategoriesAsync(db, ct);
        var departments = await SeedDepartmentsAsync(db, svcByCode, ct);
        var customers = await SeedCustomersAsync(db, rng, ct);
        var bills = await SeedBillsAsync(db, rng, customers, departments, svcByCode, ct);
        await SeedTicketsAsync(db, rng, customers, departments, bills, svcByCode, complaintByCode, resolutionByCode, ct);
    }

    // ─── TicketCategory: 10 utility-realistic bilingual categories (upsert by name) ──────────────
    private static async Task SeedTicketCategoriesAsync(ApplicationDbContext db, CancellationToken ct)
    {
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

    // ─── Departments: governorates × seeded utility services ───────────────────────────────────
    private static async Task<List<Department>> SeedDepartmentsAsync(
        ApplicationDbContext db,
        Dictionary<string, ServiceType> svcByCode,
        CancellationToken ct)
    {
        var governorates = await db.Regions
            .Where(r => r.RegionType == RegionType.Governorate)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // Generate one Department per (governorate, utility-service-type) for the standard 4 codes.
        var serviceCodes = new[]
        {
            ServiceTypeCodes.Electricity,
            ServiceTypeCodes.Internet,
            ServiceTypeCodes.Water,
            ServiceTypeCodes.Gas,
        };

        var depts = new List<Department>();
        foreach (var gov in governorates)
        {
            foreach (var code in serviceCodes)
            {
                if (!svcByCode.TryGetValue(code, out var svc)) continue;
                depts.Add(new Department
                {
                    NameEn = $"{gov.NameEn} {svc.NameEn} Department",
                    NameAr = $"إدارة {svc.NameAr} {gov.NameAr}",
                    ServiceTypeId = svc.Id,
                    RegionId = gov.Id,
                    ContactPhone = $"+963 {DeptPhone(gov.Id, svc.Id)}",
                    ContactEmail = $"{code.ToLower()}.{TransliterateGov(gov.NameEn)}@serviceops.sy",
                    IsActive = true,
                });
            }
        }
        db.Departments.AddRange(depts);
        await db.SaveChangesAsync(ct);
        return depts;
    }

    private static string TransliterateGov(string nameEn) =>
        nameEn.ToLower().Replace(" ", "").Replace("-", "");

    private static string DeptPhone(int govId, int svcId)
    {
        var n = (govId * 100 + svcId * 7) % 10000;
        return $"11 {n:0000} 100";
    }

    // ─── Customers: 200 distributed across districts, weighted by governorate population ─────────
    private static async Task<List<Customer>> SeedCustomersAsync(ApplicationDbContext db, Random rng, CancellationToken ct)
    {
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
        var first = rng.Next(2) + 1;
        var rest = rng.NextInt64(1_000_000_000L, 10_000_000_000L);
        return $"{first}{rest}";
    }

    // ─── Bills: 24 months of history per customer-service relationship ──────────────────────────
    private static async Task<List<Bill>> SeedBillsAsync(
        ApplicationDbContext db, Random rng,
        List<Customer> customers, List<Department> departments,
        Dictionary<string, ServiceType> svcByCode,
        CancellationToken ct)
    {
        var subscribesElectricity = 0.90;
        var subscribesInternet    = 0.75;
        var subscribesWater       = 0.60;
        var subscribesGas         = 0.30;

        // Department lookup by (RegionId, ServiceTypeId) — now FK-based instead of enum.
        var deptByGovSvc = departments.ToDictionary(d => (d.RegionId!.Value, d.ServiceTypeId));

        var allRegions = await db.Regions.ToListAsync(ct);
        var govById = allRegions.Where(r => r.RegionType == RegionType.Governorate).ToDictionary(r => r.Id, r => r);
        var districtToGov = allRegions.Where(r => r.RegionType == RegionType.District)
                                      .ToDictionary(r => r.Id, r => r.ParentRegionId!.Value);

        var bills = new List<Bill>();
        var billNumberSeq = new Dictionary<(int year, int month, int svcId), int>();

        // Stories 7 / 8 — bad and star departments.
        var badDeptId = departments[rng.Next(departments.Count)].Id;
        Department starDept;
        do { starDept = departments[rng.Next(departments.Count)]; } while (starDept.Id == badDeptId);
        var starDeptId = starDept.Id;

        // Story 3 prep — Homs electricity customers that get the bill spike.
        var homsGov = govById.Values.FirstOrDefault(g => g.NameEn == "Homs");
        var homsElectricityCustomers = new HashSet<int>();
        if (homsGov is not null)
        {
            var candidates = customers
                .Where(c => c.RegionId.HasValue && districtToGov.TryGetValue(c.RegionId.Value, out var gov) && gov == homsGov.Id)
                .OrderBy(c => c.NationalId)
                .Take(5)
                .Select(c => c.Id)
                .ToList();
            foreach (var id in candidates) homsElectricityCustomers.Add(id);
        }

        var electricityId = svcByCode[ServiceTypeCodes.Electricity].Id;

        foreach (var customer in customers)
        {
            if (!customer.RegionId.HasValue) continue;
            if (!districtToGov.TryGetValue(customer.RegionId.Value, out var govId)) continue;

            // Build list of subscribed services by Code, then resolve to (Code, ServiceType row).
            var services = new List<ServiceType>();
            if (rng.NextDouble() < subscribesElectricity) services.Add(svcByCode[ServiceTypeCodes.Electricity]);
            if (rng.NextDouble() < subscribesInternet)    services.Add(svcByCode[ServiceTypeCodes.Internet]);
            if (rng.NextDouble() < subscribesWater)       services.Add(svcByCode[ServiceTypeCodes.Water]);
            if (rng.NextDouble() < subscribesGas)         services.Add(svcByCode[ServiceTypeCodes.Gas]);

            foreach (var svc in services)
            {
                if (!deptByGovSvc.TryGetValue((govId, svc.Id), out var dept)) continue;
                var isBadDept = dept.Id == badDeptId;
                var isStarDept = dept.Id == starDeptId;

                for (int monthsAgo = BillHistoryMonths; monthsAgo >= 1; monthsAgo--)
                {
                    var periodStart = SeedReferenceDate.AddMonths(-monthsAgo);
                    var periodEnd   = periodStart.AddMonths(1).AddDays(-1);
                    var month = periodStart.Month;

                    var seasonal = SeasonalMultiplier(svc.Code, month);
                    var (baseAmt, usageAmt, qty, unit) = GenerateAmounts(rng, svc.Code, seasonal);

                    // Story 3 spike: Homs + electricity + Nov 2025 / Dec 2025 / Jan 2026.
                    var isHomsSpikeBill = svc.Id == electricityId
                        && homsElectricityCustomers.Contains(customer.Id)
                        && ((periodStart.Year == 2025 && (periodStart.Month == 11 || periodStart.Month == 12))
                            || (periodStart.Year == 2026 && periodStart.Month == 1));
                    if (isHomsSpikeBill) usageAmt *= 2.0m;

                    var taxes = Math.Round((baseAmt + usageAmt) * 0.11m / 100m) * 100m;
                    var total = baseAmt + usageAmt + taxes;

                    var seqKey = (periodStart.Year, periodStart.Month, svc.Id);
                    billNumberSeq.TryGetValue(seqKey, out var seq);
                    billNumberSeq[seqKey] = ++seq;

                    var billNumber = $"{ServicePrefix(svc.Code)}-{periodStart:yyyy-MM}-{seq:000000}";
                    var dueDate = periodEnd.AddDays(15);

                    var statusRoll = rng.NextDouble();
                    var overdueThreshold = isBadDept ? 0.30 : (isStarDept ? 0.04 : 0.12);
                    var paidThreshold    = isStarDept ? 0.95 : 0.70;

                    BillStatus status;
                    DateTime? paidAt = null;
                    string? paymentMethod = null;
                    if (monthsAgo == 1 && statusRoll < 0.15)
                    {
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
                        ServiceTypeId  = svc.Id,
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

        const int batchSize = 1000;
        for (int i = 0; i < bills.Count; i += batchSize)
        {
            db.Bills.AddRange(bills.Skip(i).Take(batchSize));
            await db.SaveChangesAsync(ct);
        }
        return bills;
    }

    private static double SeasonalMultiplier(string svcCode, int month) => svcCode switch
    {
        ServiceTypeCodes.Electricity => (month >= 6 && month <= 9) ? 1.30 : (month == 12 || month <= 2) ? 1.15 : 1.0,
        ServiceTypeCodes.Water       => (month >= 6 && month <= 9) ? 1.20 : 1.0,
        ServiceTypeCodes.Gas         => (month == 12 || month <= 2) ? 1.40 : 1.0,
        _ => 1.0,
    };

    private static (decimal baseAmt, decimal usageAmt, decimal qty, string unit) GenerateAmounts(Random rng, string svcCode, double seasonal)
    {
        return svcCode switch
        {
            ServiceTypeCodes.Electricity => (
                2_000m,
                Round100((decimal)(rng.Next(3_000, 48_000) * seasonal)),
                (decimal)(rng.Next(100, 800) * seasonal),
                "kWh"
            ),
            ServiceTypeCodes.Internet => (
                25_000m,
                Round100((decimal)rng.Next(5_000, 55_000)),
                rng.Next(50, 500),
                "GB"
            ),
            ServiceTypeCodes.Water => (
                1_500m,
                Round100((decimal)(rng.Next(1_500, 13_500) * seasonal)),
                (decimal)(rng.Next(10, 50) * seasonal),
                "m³"
            ),
            ServiceTypeCodes.Gas => (
                5_000m,
                Round100((decimal)(rng.Next(5_000, 25_000) * seasonal)),
                (decimal)(rng.Next(1, 8) * seasonal),
                "cyl"
            ),
            // For ad-hoc service types added by an admin (e.g. "Government process"), fall back to a
            // generic moderate-bill shape so the seeder still produces data.
            _ => (
                1_500m,
                Round100((decimal)rng.Next(2_000, 20_000)),
                rng.Next(1, 50),
                ""
            )
        };
    }

    private static decimal Round100(decimal amount) => Math.Round(amount / 100m) * 100m;

    private static string ServicePrefix(string svcCode) => svcCode switch
    {
        ServiceTypeCodes.Electricity => "ELEC",
        ServiceTypeCodes.Internet    => "INT",
        ServiceTypeCodes.Water       => "WTR",
        ServiceTypeCodes.Gas         => "GAS",
        _ => svcCode.Substring(0, Math.Min(4, svcCode.Length)).ToUpper(),
    };

    // ─── Tickets: ~80 base + story patterns ─────────────────────────────────────────────────────
    private static async Task SeedTicketsAsync(
        ApplicationDbContext db, Random rng,
        List<Customer> customers, List<Department> departments, List<Bill> bills,
        Dictionary<string, ServiceType> svcByCode,
        Dictionary<string, ComplaintType> complaintByCode,
        Dictionary<string, ResolutionType> resolutionByCode,
        CancellationToken ct)
    {
        var categories = await db.TicketCategories.ToListAsync(ct);
        var priorities = await db.TicketPriorities.ToListAsync(ct);
        var statuses   = await db.TicketStatuses.ToListAsync(ct);
        var sources    = await db.TicketSources.ToListAsync(ct);

        if (categories.Count == 0 || priorities.Count == 0 || statuses.Count == 0 || sources.Count == 0)
            return;

        var staffUserId = await db.Users.Select(u => u.Id).FirstOrDefaultAsync(ct);
        if (staffUserId is null) return;

        var allRegions = await db.Regions.ToListAsync(ct);
        var districtToGov = allRegions.Where(r => r.RegionType == RegionType.District)
                                      .ToDictionary(r => r.Id, r => r.ParentRegionId!.Value);
        var deptByGovSvc = departments.ToDictionary(d => (d.RegionId!.Value, d.ServiceTypeId));

        var tickets = new List<Ticket>();
        int ticketSeq = 1;

        var electricityId = svcByCode[ServiceTypeCodes.Electricity].Id;
        var internetId    = svcByCode[ServiceTypeCodes.Internet].Id;
        var serviceDownId = complaintByCode[ComplaintTypeCodes.ServiceDown].Id;
        var billingDisputeId = complaintByCode[ComplaintTypeCodes.BillingDispute].Id;
        var outageClearedId  = resolutionByCode.TryGetValue(ResolutionTypeCodes.OutageCleared, out var oc) ? (int?)oc.Id : null;
        var resolvedId       = resolutionByCode.TryGetValue(ResolutionTypeCodes.Resolved, out var rv) ? (int?)rv.Id : null;

        // Story 1: Aleppo internet outage.
        var aleppoGovId = allRegions.FirstOrDefault(r => r.NameEn == "Aleppo" && r.RegionType == RegionType.Governorate)?.Id;
        var aleppoInternetDept = aleppoGovId.HasValue
            ? departments.FirstOrDefault(d => d.RegionId == aleppoGovId && d.ServiceTypeId == internetId)
            : null;
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
                var resolved = i < 16; // 80% cleared by outage end
                var status = resolved
                    ? FindStatus(statuses, "Resolved", "Closed", "Completed")
                    : FindStatus(statuses, "Open", "InProgress");
                tickets.Add(new Ticket
                {
                    TicketNumber       = $"TKT-{ticketSeq++:00000}",
                    Title              = $"No internet at {c.AddressLineEn}",
                    Description        = "Internet completely down since this morning. Please restore service urgently.",
                    CategoryId         = FindCategory(categories, "Internet outage").Id,
                    PriorityId         = priorities[Math.Min(2, priorities.Count - 1)].Id,
                    StatusId           = status.Id,
                    SourceId           = sources[0].Id,
                    DepartmentId       = aleppoInternetDept.Id,
                    CreatedByUserId    = staffUserId,
                    CreatedAt          = outageStart.AddMinutes(rng.Next(0, 360)),
                    CustomerId         = c.Id,
                    ComplaintTypeId    = serviceDownId,
                    ResolutionTypeId   = resolved ? outageClearedId : null,
                    RegionId           = c.RegionId,                   // issue location = where the customer is
                    ResolvedAt         = resolved ? outageEnd.AddMinutes(rng.Next(0, 60)) : null,
                });
            }
        }

        // Story 3: Homs electricity bill-anomaly customers → BillingDispute tickets.
        var homsGovId = allRegions.FirstOrDefault(r => r.NameEn == "Homs")?.Id;
        if (homsGovId.HasValue)
        {
            var homsElectricityBills = bills
                .Where(b => b.ServiceTypeId == electricityId
                    && b.UsageAmount > 30_000m
                    && customers.Any(c => c.Id == b.CustomerId
                        && c.RegionId.HasValue && districtToGov.TryGetValue(c.RegionId.Value, out var g) && g == homsGovId))
                .Take(3)
                .ToList();

            if (deptByGovSvc.TryGetValue((homsGovId.Value, electricityId), out var homsElectricityDept))
            {
                foreach (var bill in homsElectricityBills)
                {
                    var owner = customers.First(c => c.Id == bill.CustomerId);
                    tickets.Add(new Ticket
                    {
                        TicketNumber       = $"TKT-{ticketSeq++:00000}",
                        Title              = $"Bill for {bill.PeriodStart:yyyy-MM} is much higher than usual",
                        Description        = $"My electricity bill went up to {bill.TotalAmount:N0} SYP from a normal {bill.TotalAmount / 2:N0}. Please review.",
                        CategoryId         = FindCategory(categories, "Billing dispute - amount").Id,
                        PriorityId         = priorities[Math.Min(1, priorities.Count - 1)].Id,
                        StatusId           = FindStatus(statuses, "InProgress", "Open").Id,
                        SourceId           = sources[0].Id,
                        DepartmentId       = homsElectricityDept.Id,
                        CreatedByUserId    = staffUserId,
                        CreatedAt          = bill.PeriodEnd.AddDays(rng.Next(3, 20)),
                        CustomerId         = bill.CustomerId,
                        RelatedBillId      = bill.Id,
                        ComplaintTypeId    = billingDisputeId,
                        RegionId           = owner.RegionId,
                    });
                }
            }
        }

        // Base random tickets — ~60 more, mixed.
        var serviceList = new[]
        {
            (Id: electricityId, Code: ServiceTypeCodes.Electricity),
            (Id: internetId,    Code: ServiceTypeCodes.Internet),
            (Id: svcByCode[ServiceTypeCodes.Water].Id, Code: ServiceTypeCodes.Water),
            (Id: svcByCode[ServiceTypeCodes.Gas].Id,   Code: ServiceTypeCodes.Gas),
        };
        var complaintList = complaintByCode.Values.ToList();

        for (int i = 0; i < 60; i++)
        {
            var c = customers[rng.Next(customers.Count)];
            if (!c.RegionId.HasValue) continue;
            if (!districtToGov.TryGetValue(c.RegionId.Value, out var govId)) continue;

            var svc = serviceList[rng.Next(serviceList.Length)];
            if (!deptByGovSvc.TryGetValue((govId, svc.Id), out var dept)) continue;

            var complaint = complaintList[rng.Next(complaintList.Count)];
            var statusName = rng.NextDouble() < 0.60 ? "Resolved" : (rng.NextDouble() < 0.62 ? "InProgress" : "Open");
            var status = FindStatus(statuses, statusName, "Open");
            var category = FindCategoryForComplaint(categories, complaint.Code, svc.Code);

            int? relatedBillId = null;
            if (complaint.Code == ComplaintTypeCodes.BillingDispute)
            {
                var candidate = bills.Where(b => b.CustomerId == c.Id && b.ServiceTypeId == svc.Id).LastOrDefault();
                relatedBillId = candidate?.Id;
            }

            int? resolutionId = null;
            if (status.IsClosedState)
            {
                // Most closed tickets are simply Resolved; a few NoFault / BillAdjusted.
                var roll = rng.NextDouble();
                if (roll < 0.10 && resolutionByCode.TryGetValue(ResolutionTypeCodes.NoFault, out var nf)) resolutionId = nf.Id;
                else if (roll < 0.18 && complaint.Code == ComplaintTypeCodes.BillingDispute && resolutionByCode.TryGetValue(ResolutionTypeCodes.BillAdjusted, out var ba)) resolutionId = ba.Id;
                else resolutionId = resolvedId;
            }

            tickets.Add(new Ticket
            {
                TicketNumber       = $"TKT-{ticketSeq++:00000}",
                Title              = TitleForComplaint(complaint.Code, svc.Code, c.AddressLineEn ?? "the area"),
                Description        = DescriptionForComplaint(complaint.Code, svc.Code),
                CategoryId         = category.Id,
                PriorityId         = priorities[rng.Next(priorities.Count)].Id,
                StatusId           = status.Id,
                SourceId           = sources[rng.Next(sources.Count)].Id,
                DepartmentId       = dept.Id,
                CreatedByUserId    = staffUserId,
                CreatedAt          = SeedReferenceDate.AddDays(-rng.Next(1, 180)),
                CustomerId         = c.Id,
                RelatedBillId      = relatedBillId,
                ComplaintTypeId    = complaint.Id,
                ResolutionTypeId   = resolutionId,
                RegionId           = c.RegionId,
                ResolvedAt         = status.IsClosedState ? SeedReferenceDate.AddDays(-rng.Next(1, 90)) : null,
            });
        }

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

    private static TicketCategory FindCategoryForComplaint(List<TicketCategory> all, string complaintCode, string serviceCode) =>
        (complaintCode, serviceCode) switch
        {
            (ComplaintTypeCodes.ServiceDown, ServiceTypeCodes.Internet)    => FindCategory(all, "Internet outage"),
            (ComplaintTypeCodes.ServiceDown, ServiceTypeCodes.Electricity) => FindCategory(all, "Electricity outage"),
            (ComplaintTypeCodes.ServiceDown, ServiceTypeCodes.Water)       => FindCategory(all, "Water cut"),
            (ComplaintTypeCodes.ServiceDown, ServiceTypeCodes.Gas)         => FindCategory(all, "Gas service issue"),
            (ComplaintTypeCodes.ServiceDegraded, ServiceTypeCodes.Internet) => FindCategory(all, "Internet slow speed"),
            (ComplaintTypeCodes.BillingDispute, _)  => FindCategory(all, "Billing dispute - amount"),
            (ComplaintTypeCodes.MeterIssue, _)      => FindCategory(all, "Billing dispute - meter reading"),
            (ComplaintTypeCodes.NewConnection, _)   => FindCategory(all, "New service request"),
            (ComplaintTypeCodes.Disconnection, _)   => FindCategory(all, "Service disconnection issue"),
            _                                       => FindCategory(all, "Technician visit needed"),
        };

    private static string TitleForComplaint(string complaintCode, string serviceCode, string addr) => complaintCode switch
    {
        ComplaintTypeCodes.ServiceDown        => $"{ServiceLabelEn(serviceCode)} service is completely down at {addr}",
        ComplaintTypeCodes.ServiceDegraded    => $"{ServiceLabelEn(serviceCode)} service is very slow",
        ComplaintTypeCodes.BillingDispute     => "Bill amount is incorrect — please review",
        ComplaintTypeCodes.MeterIssue         => "Meter reading appears wrong",
        ComplaintTypeCodes.NewConnection      => $"Request new {ServiceLabelEn(serviceCode)} connection",
        ComplaintTypeCodes.Disconnection      => $"{ServiceLabelEn(serviceCode)} was disconnected without notice",
        _                                      => $"{ServiceLabelEn(serviceCode)} issue",
    };

    private static string DescriptionForComplaint(string complaintCode, string serviceCode) => complaintCode switch
    {
        ComplaintTypeCodes.ServiceDown        => $"The {ServiceLabelEn(serviceCode).ToLower()} service stopped working. Need urgent restoration.",
        ComplaintTypeCodes.ServiceDegraded    => $"{ServiceLabelEn(serviceCode)} is functional but performance is unacceptably degraded.",
        ComplaintTypeCodes.BillingDispute     => "My recent bill is significantly higher than my usual amount. Please review the calculation.",
        ComplaintTypeCodes.MeterIssue         => "I believe the meter reading on my last bill does not match my actual usage. Please send a technician to verify.",
        ComplaintTypeCodes.NewConnection      => $"I would like to subscribe to {ServiceLabelEn(serviceCode).ToLower()} service at my address.",
        ComplaintTypeCodes.Disconnection      => $"{ServiceLabelEn(serviceCode)} was cut off without prior notification. Please restore.",
        _                                      => $"I need assistance with my {ServiceLabelEn(serviceCode).ToLower()} service.",
    };

    private static string ServiceLabelEn(string svcCode) => svcCode switch
    {
        ServiceTypeCodes.Electricity => "Electricity",
        ServiceTypeCodes.Internet    => "Internet",
        ServiceTypeCodes.Water       => "Water",
        ServiceTypeCodes.Gas         => "Gas",
        _                            => svcCode,
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
