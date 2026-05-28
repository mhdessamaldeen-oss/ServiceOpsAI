using ServiceOpsAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Data;

/// <summary>
/// Phase 06 seeding — Billing depth + Field Operations + Customer Voice.
/// Runs after the ticket corpus exists so we can attach WorkOrders to tickets and
/// CallLogs to outages. All seeders are idempotent — each starts with an
/// AnyAsync() guard so re-running the host doesn't double-insert.
/// </summary>
public static partial class DbSeeder
{
    private const int Phase06Seed = 220526;

    public static async Task EnsureSeedPhase06PublicAsync(ApplicationDbContext context)
        => await EnsureSeedPhase06Async(context);

    private static async Task EnsureSeedPhase06Async(ApplicationDbContext context)
    {
        await SeedCurrenciesAsync(context);
        await SeedPaymentMethodsAsync(context);
        await SeedCustomerSegmentsAsync(context);
        await SeedServicePointsAsync(context);
        await SeedServiceAccountsAsync(context);
        await SeedTariffTiersAsync(context);
        await SeedPaymentsAsync(context);
        await SeedSubsidiesAsync(context);
        await SeedAssetsAsync(context);
        await SeedTechniciansAsync(context);
        await SeedMaintenanceSchedulesAsync(context);
        await SeedWorkOrdersAsync(context);
        await SeedCallLogsAsync(context);
        await SeedOutageNotificationsAsync(context);
        await SeedSlaPoliciesAsync(context);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lookups
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedCurrenciesAsync(ApplicationDbContext ctx)
    {
        if (await ctx.Currencies.AnyAsync()) return;
        ctx.Currencies.AddRange(
            new Currency { Code = "SYP", NameEn = "Syrian Pound",  NameAr = "ليرة سورية",   Symbol = "ل.س", IsBase = true,  ExchangeRateToBase = 1m,        LastRateUpdate = DateTime.UtcNow },
            new Currency { Code = "USD", NameEn = "US Dollar",     NameAr = "دولار أمريكي", Symbol = "$",   IsBase = false, ExchangeRateToBase = 13500m,    LastRateUpdate = DateTime.UtcNow },
            new Currency { Code = "EUR", NameEn = "Euro",          NameAr = "يورو",          Symbol = "€",   IsBase = false, ExchangeRateToBase = 14700m,    LastRateUpdate = DateTime.UtcNow },
            new Currency { Code = "TRY", NameEn = "Turkish Lira",  NameAr = "ليرة تركية",    Symbol = "₺",   IsBase = false, ExchangeRateToBase = 410m,      LastRateUpdate = DateTime.UtcNow }
        );
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedPaymentMethodsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.PaymentMethods.AnyAsync()) return;
        ctx.PaymentMethods.AddRange(
            new PaymentMethod { Code = PaymentMethodCodes.Cash,         NameEn = "Cash",            NameAr = "نقدي",                IsDigital = false, FeePercent = 0m,    SortOrder = 1 },
            new PaymentMethod { Code = PaymentMethodCodes.BankTransfer, NameEn = "Bank Transfer",   NameAr = "حوالة بنكية",        IsDigital = true,  FeePercent = 0.5m,  SortOrder = 2 },
            new PaymentMethod { Code = PaymentMethodCodes.Card,         NameEn = "Card",            NameAr = "بطاقة",               IsDigital = true,  FeePercent = 1.2m,  SortOrder = 3 },
            new PaymentMethod { Code = PaymentMethodCodes.MobileWallet, NameEn = "Mobile Wallet",   NameAr = "محفظة موبايل",       IsDigital = true,  FeePercent = 0.8m,  SortOrder = 4 },
            new PaymentMethod { Code = PaymentMethodCodes.OnlineWallet, NameEn = "Online Wallet",   NameAr = "محفظة إلكترونية",    IsDigital = true,  FeePercent = 1.0m,  SortOrder = 5 }
        );
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedCustomerSegmentsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.CustomerSegments.AnyAsync()) return;
        ctx.CustomerSegments.AddRange(
            new CustomerSegment { Code = CustomerSegmentCodes.Residential, NameEn = "Residential", NameAr = "سكني",     IsSubsidyEligible = true,  DefaultPriorityFloor = 1, SortOrder = 1 },
            new CustomerSegment { Code = CustomerSegmentCodes.Commercial,  NameEn = "Commercial",  NameAr = "تجاري",     IsSubsidyEligible = false, DefaultPriorityFloor = 2, SortOrder = 2 },
            new CustomerSegment { Code = CustomerSegmentCodes.Industrial,  NameEn = "Industrial",  NameAr = "صناعي",     IsSubsidyEligible = false, DefaultPriorityFloor = 3, SortOrder = 3 },
            new CustomerSegment { Code = CustomerSegmentCodes.Government,  NameEn = "Government",  NameAr = "حكومي",     IsSubsidyEligible = false, DefaultPriorityFloor = 3, SortOrder = 4 },
            new CustomerSegment { Code = CustomerSegmentCodes.Displaced,   NameEn = "Displaced",   NameAr = "مهجَّر",     IsSubsidyEligible = true,  DefaultPriorityFloor = 2, SortOrder = 5 }
        );
        await ctx.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Billing depth: ServicePoints, ServiceAccounts, TariffTiers, Payments, Subsidies
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedServicePointsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.ServicePoints.AnyAsync()) return;

        var districts = await ctx.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.Id).ToListAsync();
        if (districts.Count == 0) return;

        var rng = new Random(Phase06Seed);
        var points = new List<ServicePoint>();
        var districtStreets = new[] { "Al-Mazzeh", "Bab Touma", "Mezzeh 86", "Kafr Sousseh", "Al-Midan", "Salihiyah", "Al-Hamidiyah", "Yarmouk", "Al-Halbouni", "Souq Saroujah" };
        var districtStreetsAr = new[] { "المزة", "باب توما", "مزة 86", "كفر سوسة", "الميدان", "الصالحية", "الحميدية", "اليرموك", "الحلبوني", "سوق ساروجة" };

        int idx = 0;
        foreach (var d in districts)
        {
            int pointsHere = rng.Next(3, 7);
            for (int i = 0; i < pointsHere; i++)
            {
                idx++;
                var streetIdx = rng.Next(districtStreets.Length);
                var typeRoll = rng.NextDouble();
                var ptype = typeRoll < 0.7 ? ServicePointType.Residential
                          : typeRoll < 0.85 ? ServicePointType.Commercial
                          : typeRoll < 0.95 ? ServicePointType.Mixed
                          : ServicePointType.Industrial;
                points.Add(new ServicePoint
                {
                    PointCode = $"SP-{d.Id:D3}-{idx:D5}",
                    RegionId = d.Id,
                    AddressLineEn = $"{districtStreets[streetIdx]} St., Bldg {rng.Next(1, 250)}, {d.NameEn}",
                    AddressLineAr = $"شارع {districtStreetsAr[streetIdx]}، بناء {rng.Next(1, 250)}، {d.NameAr}",
                    MeterNumber = $"M-{rng.Next(100000, 999999)}",
                    Latitude = 33.5m + (decimal)(rng.NextDouble() * 3.5),
                    Longitude = 36.0m + (decimal)(rng.NextDouble() * 4.0),
                    PointType = ptype,
                    InstalledAt = DateTime.UtcNow.AddYears(-rng.Next(1, 12)).AddDays(-rng.Next(0, 365)),
                    IsActive = true
                });
            }
        }
        ctx.ServicePoints.AddRange(points);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedServiceAccountsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.ServiceAccounts.AnyAsync()) return;

        var customers = await ctx.Customers.OrderBy(c => c.Id).ToListAsync();
        var serviceTypes = await ctx.ServiceTypes.ToListAsync();
        var points = await ctx.ServicePoints.OrderBy(p => p.Id).ToListAsync();
        var segments = await ctx.CustomerSegments.ToListAsync();
        var departments = await ctx.Departments.ToListAsync();
        if (customers.Count == 0 || serviceTypes.Count == 0 || points.Count == 0 || segments.Count == 0) return;

        var rng = new Random(Phase06Seed + 1);
        var accounts = new List<ServiceAccount>();
        int idx = 0;
        foreach (var c in customers)
        {
            // 1-3 service accounts per customer; bias to 1-2.
            int n = rng.NextDouble() < 0.5 ? 1 : (rng.NextDouble() < 0.85 ? 2 : 3);
            var usedServiceIds = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                var svc = serviceTypes[rng.Next(serviceTypes.Count)];
                if (!usedServiceIds.Add(svc.Id)) continue;     // avoid duplicate (customer, service) when picking again

                idx++;
                var sp = points[rng.Next(points.Count)];
                var segPicker = rng.NextDouble();
                var seg = segPicker < 0.7 ? segments.First(s => s.Code == CustomerSegmentCodes.Residential)
                        : segPicker < 0.85 ? segments.First(s => s.Code == CustomerSegmentCodes.Commercial)
                        : segPicker < 0.92 ? segments.First(s => s.Code == CustomerSegmentCodes.Displaced)
                        : segPicker < 0.97 ? segments.First(s => s.Code == CustomerSegmentCodes.Industrial)
                        : segments.First(s => s.Code == CustomerSegmentCodes.Government);

                var dept = departments.Where(d => d.ServiceTypeId == svc.Id).OrderBy(_ => rng.Next()).FirstOrDefault();

                var status = c.Status switch
                {
                    CustomerStatus.Churned   => ServiceAccountStatus.Terminated,
                    CustomerStatus.Suspended => ServiceAccountStatus.Suspended,
                    _ => rng.NextDouble() < 0.05 ? ServiceAccountStatus.Suspended : ServiceAccountStatus.Active
                };

                var activated = c.SignupAt.AddDays(rng.Next(0, 60));
                accounts.Add(new ServiceAccount
                {
                    AccountNumber = $"ACC-{activated:yyyy}-{idx:D6}",
                    CustomerId = c.Id,
                    ServiceTypeId = svc.Id,
                    ServicePointId = sp.Id,
                    CustomerSegmentId = seg.Id,
                    DepartmentId = dept?.Id,
                    ActivatedAt = activated,
                    DeactivatedAt = status == ServiceAccountStatus.Terminated ? c.ChurnedAt : null,
                    Status = status,
                    ContractedCapacity = svc.Code switch
                    {
                        ServiceTypeCodes.Electricity => rng.Next(5, 60),
                        ServiceTypeCodes.Internet    => new[] { 25, 50, 100, 200 }[rng.Next(4)],
                        ServiceTypeCodes.Water       => rng.Next(10, 50),
                        ServiceTypeCodes.Gas         => rng.Next(2, 20),
                        _ => null
                    },
                    CapacityUnit = svc.Code switch
                    {
                        ServiceTypeCodes.Electricity => "kW",
                        ServiceTypeCodes.Internet    => "Mbps",
                        ServiceTypeCodes.Water       => "m³/day",
                        ServiceTypeCodes.Gas         => "cyl/mo",
                        _ => null
                    }
                });
            }
        }
        ctx.ServiceAccounts.AddRange(accounts);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedTariffTiersAsync(ApplicationDbContext ctx)
    {
        if (await ctx.TariffTiers.AnyAsync()) return;
        var tariffs = await ctx.Tariffs.Include(t => t.ServiceType).ToListAsync();
        if (tariffs.Count == 0) return;

        var tiers = new List<TariffTier>();
        foreach (var t in tariffs)
        {
            // Three-tier block pricing: lifeline / standard / premium.
            // Brackets vary slightly by service type.
            (decimal from1, decimal to1, decimal r1,
             decimal from2, decimal to2, decimal r2,
             decimal from3, decimal r3) brackets = t.ServiceType?.Code switch
            {
                ServiceTypeCodes.Electricity => (0,   100, t.RatePerUnit * 0.6m, 100, 300, t.RatePerUnit, 300, t.RatePerUnit * 1.8m),
                ServiceTypeCodes.Water       => (0,   15,  t.RatePerUnit * 0.5m, 15,  40,  t.RatePerUnit, 40,  t.RatePerUnit * 2.0m),
                ServiceTypeCodes.Gas         => (0,   5,   t.RatePerUnit * 0.7m, 5,   15,  t.RatePerUnit, 15,  t.RatePerUnit * 1.5m),
                ServiceTypeCodes.Internet    => (0,   50,  t.RatePerUnit,        50,  500, t.RatePerUnit * 0.7m, 500, t.RatePerUnit * 0.5m),
                _                            => (0,   50,  t.RatePerUnit * 0.7m, 50,  150, t.RatePerUnit, 150, t.RatePerUnit * 1.6m)
            };

            tiers.Add(new TariffTier { TariffId = t.Id, TierNumber = 1, FromUnit = brackets.from1, ToUnit = brackets.to1, RatePerUnit = brackets.r1, LabelEn = "Lifeline", LabelAr = "الشريحة الأولى" });
            tiers.Add(new TariffTier { TariffId = t.Id, TierNumber = 2, FromUnit = brackets.from2, ToUnit = brackets.to2, RatePerUnit = brackets.r2, LabelEn = "Standard", LabelAr = "الشريحة الثانية" });
            tiers.Add(new TariffTier { TariffId = t.Id, TierNumber = 3, FromUnit = brackets.from3, ToUnit = null,         RatePerUnit = brackets.r3, LabelEn = "Premium",  LabelAr = "الشريحة الثالثة" });
        }
        ctx.TariffTiers.AddRange(tiers);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedPaymentsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.Payments.AnyAsync()) return;

        var bills = await ctx.Bills.Where(b => b.Status == BillStatus.Paid || b.Status == BillStatus.Overdue).OrderBy(b => b.Id).ToListAsync();
        if (bills.Count == 0) return;

        var accounts = await ctx.ServiceAccounts.AsNoTracking().ToListAsync();
        var methods = await ctx.PaymentMethods.AsNoTracking().ToListAsync();
        var currencies = await ctx.Currencies.AsNoTracking().ToListAsync();
        var baseCurrency = currencies.First(c => c.IsBase);

        var rng = new Random(Phase06Seed + 2);
        var payments = new List<Payment>();
        int idx = 0;
        foreach (var b in bills)
        {
            var matchedAccount = accounts.FirstOrDefault(a => a.CustomerId == b.CustomerId && a.ServiceTypeId == b.ServiceTypeId);

            // For PAID bills: 70% single-payment, 25% two-installments, 5% three-installments.
            // For OVERDUE bills: 0-1 partial payment.
            int installments = b.Status == BillStatus.Paid
                ? (rng.NextDouble() < 0.7 ? 1 : rng.NextDouble() < 0.85 ? 2 : 3)
                : (rng.NextDouble() < 0.3 ? 1 : 0);

            if (installments == 0) continue;

            decimal remaining = b.TotalAmount;
            DateTime payDate = b.IssuedAt.AddDays(rng.Next(1, 25));
            for (int i = 0; i < installments; i++)
            {
                idx++;
                bool lastInstallment = i == installments - 1;
                decimal portion = lastInstallment ? remaining : Math.Round(remaining * (decimal)(0.3 + rng.NextDouble() * 0.4), 2);
                remaining -= portion;

                // Currency picker — most payments in SYP; small minority in USD/TRY (border region) / EUR (NGO subsidies).
                var crPicker = rng.NextDouble();
                var cr = crPicker < 0.88 ? baseCurrency
                       : crPicker < 0.95 ? currencies.First(c => c.Code == CurrencyCodes.USD)
                       : crPicker < 0.98 ? currencies.First(c => c.Code == CurrencyCodes.TRY)
                       : currencies.First(c => c.Code == CurrencyCodes.EUR);

                decimal amountInPayCurrency = cr.IsBase ? portion : Math.Round(portion / cr.ExchangeRateToBase, 2);

                var methodPicker = rng.NextDouble();
                var method = methodPicker < 0.45 ? methods.First(m => m.Code == PaymentMethodCodes.Cash)
                           : methodPicker < 0.70 ? methods.First(m => m.Code == PaymentMethodCodes.BankTransfer)
                           : methodPicker < 0.88 ? methods.First(m => m.Code == PaymentMethodCodes.MobileWallet)
                           : methodPicker < 0.96 ? methods.First(m => m.Code == PaymentMethodCodes.Card)
                           : methods.First(m => m.Code == PaymentMethodCodes.OnlineWallet);

                payments.Add(new Payment
                {
                    PaymentReference = $"PAY-{payDate:yyyy-MM}-{idx:D7}",
                    BillId = b.Id,
                    ServiceAccountId = matchedAccount?.Id,
                    PaymentMethodId = method.Id,
                    CurrencyId = cr.Id,
                    Amount = amountInPayCurrency,
                    ExchangeRateToBase = cr.ExchangeRateToBase,
                    AmountInBase = portion,
                    Status = PaymentStatus.Posted,
                    PaidAt = payDate,
                    ExternalTransactionId = method.IsDigital ? $"TXN-{rng.Next(100000000, 999999999)}" : null,
                    Notes = installments > 1 ? $"Installment {i + 1} of {installments}" : null
                });

                payDate = payDate.AddDays(rng.Next(7, 30));
            }
        }
        ctx.Payments.AddRange(payments);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedSubsidiesAsync(ApplicationDbContext ctx)
    {
        if (await ctx.Subsidies.AnyAsync()) return;

        var displacedSegment = await ctx.CustomerSegments.FirstOrDefaultAsync(s => s.Code == CustomerSegmentCodes.Displaced);
        var residentialSegment = await ctx.CustomerSegments.FirstOrDefaultAsync(s => s.Code == CustomerSegmentCodes.Residential);
        if (displacedSegment is null || residentialSegment is null) return;

        var displacedAccounts = await ctx.ServiceAccounts.AsNoTracking()
            .Where(a => a.CustomerSegmentId == displacedSegment.Id)
            .ToListAsync();
        var lowIncomeAccounts = await ctx.ServiceAccounts.AsNoTracking()
            .Where(a => a.CustomerSegmentId == residentialSegment.Id)
            .OrderBy(a => a.Id)
            .Take(40)
            .ToListAsync();

        var rng = new Random(Phase06Seed + 3);
        var subsidies = new List<Subsidy>();

        foreach (var a in displacedAccounts.Concat(lowIncomeAccounts))
        {
            var bills = await ctx.Bills.AsNoTracking()
                .Where(b => b.CustomerId == a.CustomerId && b.ServiceTypeId == a.ServiceTypeId)
                .OrderBy(b => b.PeriodStart)
                .Take(rng.Next(1, 4))
                .ToListAsync();

            string program = a.CustomerSegmentId == displacedSegment.Id ? "DISPLACED-2026" : "LOW-INCOME-RES";
            string nameEn = a.CustomerSegmentId == displacedSegment.Id ? "Displaced Family Relief 2026" : "Low-Income Residential Subsidy";
            string nameAr = a.CustomerSegmentId == displacedSegment.Id ? "إعانة الأسر المهجَّرة 2026" : "دعم السكني محدود الدخل";
            decimal pct = a.CustomerSegmentId == displacedSegment.Id ? 50m : 20m;

            foreach (var b in bills)
            {
                decimal amount = Math.Round(b.TotalAmount * (pct / 100m), 2);
                subsidies.Add(new Subsidy
                {
                    BillId = b.Id,
                    CustomerId = a.CustomerId,
                    CustomerSegmentId = a.CustomerSegmentId,
                    ProgramCode = program,
                    ProgramNameEn = nameEn,
                    ProgramNameAr = nameAr,
                    Amount = amount,
                    AppliedPercent = pct,
                    IssuedAt = b.IssuedAt.AddDays(rng.Next(0, 14)),
                    Status = rng.NextDouble() < 0.95 ? SubsidyStatus.Applied : SubsidyStatus.Revoked
                });
            }
        }
        ctx.Subsidies.AddRange(subsidies);
        await ctx.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Field Operations: Assets, Technicians, MaintenanceSchedules, WorkOrders
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedAssetsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.Assets.AnyAsync()) return;

        var serviceTypes = await ctx.ServiceTypes.AsNoTracking().ToListAsync();
        var districts = await ctx.Regions.Where(r => r.RegionType == RegionType.District).ToListAsync();
        var departments = await ctx.Departments.AsNoTracking().ToListAsync();
        if (serviceTypes.Count == 0 || districts.Count == 0) return;

        var rng = new Random(Phase06Seed + 4);
        var assets = new List<Asset>();
        int idx = 0;

        (AssetType T, string EnPrefix, string ArPrefix, string Spec)[] electricityAssets =
        {
            (AssetType.Substation,  "Substation",  "محطة فرعية",   "33/11 kV, 25 MVA"),
            (AssetType.Transformer, "Transformer", "محوّل",          "11 kV / 0.4 kV, 1250 kVA"),
            (AssetType.PowerLine,   "Feeder Line", "خط تغذية",      "11 kV overhead, 6.4 km"),
            (AssetType.Generator,   "Generator",   "مولّد كهربائي", "Diesel 800 kVA — district backup")
        };
        (AssetType T, string EnPrefix, string ArPrefix, string Spec)[] waterAssets =
        {
            (AssetType.PumpingStation, "Pumping Station", "محطة ضخ",       "Centrifugal, 250 m³/h"),
            (AssetType.WaterPipeline,  "Main Pipeline",   "خط مياه رئيسي", "DN 400, ductile iron, 2.1 km")
        };
        (AssetType T, string EnPrefix, string ArPrefix, string Spec)[] gasAssets =
        {
            (AssetType.GasRegulator,  "Gas Regulator",    "منظِّم غاز",       "MR/MD 50, 4 bar to 0.1 bar")
        };
        (AssetType T, string EnPrefix, string ArPrefix, string Spec)[] internetAssets =
        {
            (AssetType.DslamCabinet, "DSLAM Cabinet", "خزانة DSLAM",  "192-port, VDSL2"),
            (AssetType.FiberNode,    "Fiber Node",    "عقدة ألياف",     "GPON OLT, 1:64 split")
        };

        foreach (var svc in serviceTypes)
        {
            var pool = svc.Code switch
            {
                ServiceTypeCodes.Electricity => electricityAssets,
                ServiceTypeCodes.Water       => waterAssets,
                ServiceTypeCodes.Gas         => gasAssets,
                ServiceTypeCodes.Internet    => internetAssets,
                _ => Array.Empty<(AssetType T, string EnPrefix, string ArPrefix, string Spec)>()
            };
            if (pool.Length == 0) continue;

            var dept = departments.FirstOrDefault(d => d.ServiceTypeId == svc.Id);

            int total = svc.Code == ServiceTypeCodes.Electricity ? 22 : svc.Code == ServiceTypeCodes.Water ? 12 : svc.Code == ServiceTypeCodes.Internet ? 14 : 6;
            for (int i = 0; i < total; i++)
            {
                var d = districts[rng.Next(districts.Count)];
                var pick = pool[rng.Next(pool.Length)];
                idx++;
                var statusRoll = rng.NextDouble();
                var status = statusRoll < 0.85 ? AssetStatus.Operational
                           : statusRoll < 0.92 ? AssetStatus.UnderMaintenance
                           : statusRoll < 0.97 ? AssetStatus.Faulty
                           : AssetStatus.Decommissioned;
                assets.Add(new Asset
                {
                    AssetCode = $"{pick.T.ToString().Substring(0, Math.Min(4, pick.T.ToString().Length)).ToUpperInvariant()}-{d.Id:D3}-{idx:D4}",
                    NameEn = $"{pick.EnPrefix} {d.NameEn} #{i + 1}",
                    NameAr = $"{pick.ArPrefix} {d.NameAr} رقم {i + 1}",
                    ServiceTypeId = svc.Id,
                    RegionId = d.Id,
                    DepartmentId = dept?.Id,
                    AssetType = pick.T,
                    Status = status,
                    CommissionedAt = DateTime.UtcNow.AddYears(-rng.Next(2, 25)),
                    Specification = pick.Spec,
                    Latitude = 33.5m + (decimal)(rng.NextDouble() * 3.5),
                    Longitude = 36.0m + (decimal)(rng.NextDouble() * 4.0)
                });
            }
        }
        ctx.Assets.AddRange(assets);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedTechniciansAsync(ApplicationDbContext ctx)
    {
        if (await ctx.Technicians.AnyAsync()) return;

        var depts = await ctx.Departments.AsNoTracking().ToListAsync();
        var districts = await ctx.Regions.Where(r => r.RegionType == RegionType.District).ToListAsync();
        if (depts.Count == 0) return;

        var rng = new Random(Phase06Seed + 5);
        var firstNamesEn = new[] { "Ahmad", "Khaled", "Omar", "Yusuf", "Bashar", "Maher", "Hisham", "Samer", "Ziad", "Wael", "Fadi", "Issam", "Nizar", "Tarek" };
        var firstNamesAr = new[] { "أحمد", "خالد", "عمر", "يوسف", "بشار", "ماهر", "هشام", "سامر", "زياد", "وائل", "فادي", "عصام", "نزار", "طارق" };
        var lastNamesEn = new[] { "Al-Sayyed", "Al-Khoury", "Hammoud", "Saleh", "Asaad", "Jaber", "Najjar", "Haddad", "Mansour", "Hariri", "Khalil" };
        var lastNamesAr = new[] { "السيد", "الخوري", "حمود", "صالح", "أسعد", "جابر", "نجار", "حداد", "منصور", "حريري", "خليل" };

        var techs = new List<Technician>();
        int idx = 0;
        foreach (var d in depts)
        {
            int n = rng.Next(2, 6);
            for (int i = 0; i < n; i++)
            {
                idx++;
                int fi = rng.Next(firstNamesEn.Length);
                int li = rng.Next(lastNamesEn.Length);
                var specialty = d.ServiceTypeId == 0 ? TechnicianSpecialty.General : TechnicianSpecialty.General;
                // Map by department's ServiceType code if loadable.
                var svc = await ctx.ServiceTypes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == d.ServiceTypeId);
                if (svc != null)
                {
                    specialty = svc.Code switch
                    {
                        ServiceTypeCodes.Electricity => TechnicianSpecialty.Electrical,
                        ServiceTypeCodes.Water       => TechnicianSpecialty.Plumbing,
                        ServiceTypeCodes.Gas         => TechnicianSpecialty.Gas,
                        ServiceTypeCodes.Internet    => TechnicianSpecialty.Telecom,
                        _ => TechnicianSpecialty.General
                    };
                }
                techs.Add(new Technician
                {
                    EmployeeCode = $"TEC-{idx:D5}",
                    FullNameEn = $"{firstNamesEn[fi]} {lastNamesEn[li]}",
                    FullNameAr = $"{firstNamesAr[fi]} {lastNamesAr[li]}",
                    Phone = $"+9639{rng.Next(10000000, 99999999)}",
                    DepartmentId = d.Id,
                    PrimaryRegionId = d.RegionId ?? districts[rng.Next(districts.Count)].Id,
                    Specialty = specialty,
                    YearsOfExperience = rng.Next(1, 22),
                    HiredAt = DateTime.UtcNow.AddYears(-rng.Next(1, 18)).AddDays(-rng.Next(0, 365)),
                    IsActive = rng.NextDouble() < 0.93
                });
            }
        }
        ctx.Technicians.AddRange(techs);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedMaintenanceSchedulesAsync(ApplicationDbContext ctx)
    {
        if (await ctx.MaintenanceSchedules.AnyAsync()) return;
        var assets = await ctx.Assets.AsNoTracking().ToListAsync();
        if (assets.Count == 0) return;

        var rng = new Random(Phase06Seed + 6);
        var schedules = new List<MaintenanceSchedule>();
        // Schedule maintenance for ~70% of operational assets, spread over ±90 days from today.
        foreach (var a in assets.Where(a => a.Status != AssetStatus.Decommissioned))
        {
            if (rng.NextDouble() > 0.7) continue;
            var planned = DateTime.UtcNow.AddDays(rng.Next(-90, 90));
            var duration = TimeSpan.FromHours(rng.Next(2, 12));
            var status = planned < DateTime.UtcNow.AddDays(-1)
                ? (rng.NextDouble() < 0.85 ? MaintenanceStatus.Completed : MaintenanceStatus.Cancelled)
                : MaintenanceStatus.Scheduled;
            var mtype = (MaintenanceType)rng.Next(1, 5);

            schedules.Add(new MaintenanceSchedule
            {
                ScheduleNumber = $"MS-{planned:yyyy-MM}-{schedules.Count + 1:D4}",
                AssetId = a.Id,
                RegionId = a.RegionId,
                DepartmentId = a.DepartmentId,
                ScheduledStart = planned,
                ScheduledEnd = planned.Add(duration),
                ActualStart = status == MaintenanceStatus.Completed ? planned.AddMinutes(rng.Next(-30, 60)) : null,
                ActualEnd = status == MaintenanceStatus.Completed ? planned.Add(duration).AddMinutes(rng.Next(-60, 90)) : null,
                Status = status,
                MaintenanceType = mtype,
                TitleEn = $"{mtype} maintenance — {a.NameEn}",
                TitleAr = $"صيانة {mtype} — {a.NameAr}",
                Description = "Routine inspection per quarterly schedule. Documents updated post-visit.",
                ExpectedAffectedCustomers = rng.Next(0, 600),
                CustomersNotified = rng.NextDouble() < 0.75
            });
        }
        ctx.MaintenanceSchedules.AddRange(schedules);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedWorkOrdersAsync(ApplicationDbContext ctx)
    {
        if (await ctx.WorkOrders.AnyAsync()) return;
        var tickets = await ctx.Tickets.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var outages = await ctx.Outages.AsNoTracking().ToListAsync();
        var assets = await ctx.Assets.AsNoTracking().ToListAsync();
        var techs = await ctx.Technicians.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        var points = await ctx.ServicePoints.AsNoTracking().ToListAsync();
        if (techs.Count == 0) return;

        var rng = new Random(Phase06Seed + 7);
        var orders = new List<WorkOrder>();
        int idx = 0;

        // Reactive: from tickets that look field-dispatchable (~40% of tickets).
        foreach (var t in tickets)
        {
            if (rng.NextDouble() > 0.4) continue;
            idx++;
            var tech = techs[rng.Next(techs.Count)];
            var asset = assets.Where(a => a.RegionId == t.RegionId || t.RegionId == null).OrderBy(_ => rng.Next()).FirstOrDefault();
            var status = rng.NextDouble() switch
            {
                < 0.55 => WorkOrderStatus.Completed,
                < 0.75 => WorkOrderStatus.InProgress,
                < 0.85 => WorkOrderStatus.Assigned,
                < 0.95 => WorkOrderStatus.OnHold,
                _      => WorkOrderStatus.Cancelled
            };
            var created = t.CreatedAt.AddMinutes(rng.Next(15, 180));
            DateTime? dispatched = created.AddMinutes(rng.Next(10, 240));
            DateTime? arrived = status >= WorkOrderStatus.InProgress ? dispatched?.AddMinutes(rng.Next(20, 180)) : null;
            DateTime? completed = status == WorkOrderStatus.Completed ? arrived?.AddMinutes(rng.Next(30, 480)) : null;

            orders.Add(new WorkOrder
            {
                OrderNumber = $"WO-{created:yyyy-MM}-{idx:D6}",
                OrderType = WorkOrderType.Reactive,
                Status = status,
                Priority = (WorkOrderPriority)rng.Next(1, 5),
                TicketId = t.Id,
                AssetId = asset?.Id,
                ServicePointId = points.Count > 0 ? points[rng.Next(points.Count)].Id : null,
                DepartmentId = t.DepartmentId,
                RegionId = t.RegionId,
                AssignedTechnicianId = tech.Id,
                CreatedAt = created,
                DispatchedAt = dispatched,
                ArrivedOnSiteAt = arrived,
                CompletedAt = completed,
                TitleEn = $"Reactive work — Ticket {t.TicketNumber}",
                TitleAr = $"إجراء استجابة — شكوى {t.TicketNumber}",
                Description = t.Title,
                ResolutionNotes = completed.HasValue ? "Field issue addressed; customer verified service restored." : null,
                RequiredSecondVisit = rng.NextDouble() < 0.12
            });
        }

        // Reactive: from outages — one work order per outage typically, occasionally two.
        foreach (var o in outages)
        {
            int n = rng.NextDouble() < 0.25 ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                idx++;
                var tech = techs[rng.Next(techs.Count)];
                var asset = assets.Where(a => a.RegionId == o.RegionId).OrderBy(_ => rng.Next()).FirstOrDefault();
                var status = o.EndedAt.HasValue ? WorkOrderStatus.Completed : WorkOrderStatus.InProgress;
                orders.Add(new WorkOrder
                {
                    OrderNumber = $"WO-{o.StartedAt:yyyy-MM}-{idx:D6}",
                    OrderType = WorkOrderType.Reactive,
                    Status = status,
                    Priority = o.Severity switch
                    {
                        OutageSeverity.Critical => WorkOrderPriority.Critical,
                        OutageSeverity.Major    => WorkOrderPriority.High,
                        _                       => WorkOrderPriority.Normal
                    },
                    OutageId = o.Id,
                    AssetId = asset?.Id,
                    DepartmentId = o.DepartmentId,
                    RegionId = o.RegionId,
                    AssignedTechnicianId = tech.Id,
                    CreatedAt = o.StartedAt.AddMinutes(rng.Next(5, 30)),
                    DispatchedAt = o.StartedAt.AddMinutes(rng.Next(15, 60)),
                    ArrivedOnSiteAt = o.StartedAt.AddMinutes(rng.Next(45, 180)),
                    CompletedAt = o.EndedAt,
                    TitleEn = $"Outage response — {o.OutageNumber}",
                    TitleAr = $"استجابة انقطاع — {o.OutageNumber}",
                    Description = o.TitleEn ?? "Outage field response",
                    ResolutionNotes = o.EndedAt.HasValue ? "Service restored. Root cause logged." : null
                });
            }
        }

        // Preventive: a handful per asset.
        foreach (var a in assets.Where(a => a.Status == AssetStatus.Operational))
        {
            if (rng.NextDouble() > 0.3) continue;
            idx++;
            var tech = techs[rng.Next(techs.Count)];
            var created = DateTime.UtcNow.AddDays(-rng.Next(2, 365));
            var completed = created.AddHours(rng.Next(2, 9));
            orders.Add(new WorkOrder
            {
                OrderNumber = $"WO-{created:yyyy-MM}-{idx:D6}",
                OrderType = WorkOrderType.Preventive,
                Status = WorkOrderStatus.Completed,
                Priority = WorkOrderPriority.Low,
                AssetId = a.Id,
                DepartmentId = a.DepartmentId,
                RegionId = a.RegionId,
                AssignedTechnicianId = tech.Id,
                CreatedAt = created,
                DispatchedAt = created.AddMinutes(rng.Next(10, 90)),
                ArrivedOnSiteAt = created.AddMinutes(rng.Next(40, 180)),
                CompletedAt = completed,
                TitleEn = $"Preventive maintenance — {a.NameEn}",
                TitleAr = $"صيانة وقائية — {a.NameAr}",
                Description = "Routine inspection per maintenance plan."
            });
        }
        ctx.WorkOrders.AddRange(orders);
        await ctx.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Customer Voice: CallLogs, OutageNotifications, SlaPolicies
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedCallLogsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.CallLogs.AnyAsync()) return;

        var customers = await ctx.Customers.AsNoTracking().ToListAsync();
        var tickets = await ctx.Tickets.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var outages = await ctx.Outages.AsNoTracking().ToListAsync();
        if (customers.Count == 0) return;

        var rng = new Random(Phase06Seed + 8);
        var logs = new List<CallLog>();
        int idx = 0;

        // Calls preceding a ticket: ~60% of tickets have a precursor call.
        foreach (var t in tickets)
        {
            if (rng.NextDouble() > 0.6) continue;
            idx++;
            var started = t.CreatedAt.AddMinutes(-rng.Next(2, 30));
            var dur = rng.Next(45, 600);
            logs.Add(new CallLog
            {
                CallReference = $"CL-{started:yyyy-MM-dd}-{idx:D6}",
                CustomerId = t.CustomerId,
                CallerPhone = null,
                Direction = ContactDirection.Inbound,
                Channel = rng.NextDouble() < 0.7 ? ContactChannel.Phone : (rng.NextDouble() < 0.5 ? ContactChannel.Whatsapp : ContactChannel.Sms),
                StartedAt = started,
                EndedAt = started.AddSeconds(dur),
                DurationSeconds = dur,
                Outcome = ContactOutcome.EscalatedToTicket,
                RelatedTicketId = t.Id,
                Summary = "Customer described the issue; agent confirmed details and opened a ticket."
            });
        }

        // Calls during outages — surge in inbound contacts.
        foreach (var o in outages)
        {
            int callsThisOutage = (o.AffectedCustomerCount ?? 100) / rng.Next(8, 20);
            callsThisOutage = Math.Min(callsThisOutage, 30);
            for (int i = 0; i < callsThisOutage; i++)
            {
                idx++;
                var c = customers[rng.Next(customers.Count)];
                var started = o.StartedAt.AddMinutes(rng.Next(0, 240));
                var dur = rng.Next(30, 420);
                logs.Add(new CallLog
                {
                    CallReference = $"CL-{started:yyyy-MM-dd}-{idx:D6}",
                    CustomerId = c.Id,
                    Direction = ContactDirection.Inbound,
                    Channel = ContactChannel.Phone,
                    StartedAt = started,
                    EndedAt = started.AddSeconds(dur),
                    DurationSeconds = dur,
                    Outcome = rng.NextDouble() < 0.7 ? ContactOutcome.Resolved : ContactOutcome.CallbackScheduled,
                    RelatedOutageId = o.Id,
                    Summary = "Outage status query — agent provided ETA per restoration schedule."
                });
            }
        }

        // Unrelated everyday calls.
        for (int i = 0; i < 250; i++)
        {
            idx++;
            var c = customers[rng.Next(customers.Count)];
            var started = DateTime.UtcNow.AddDays(-rng.Next(1, 180)).AddMinutes(rng.Next(0, 1440));
            var dur = rng.Next(30, 360);
            logs.Add(new CallLog
            {
                CallReference = $"CL-{started:yyyy-MM-dd}-{idx:D6}",
                CustomerId = c.Id,
                Direction = rng.NextDouble() < 0.85 ? ContactDirection.Inbound : ContactDirection.Outbound,
                Channel = (ContactChannel)rng.Next(1, 7),
                StartedAt = started,
                EndedAt = started.AddSeconds(dur),
                DurationSeconds = dur,
                Outcome = (ContactOutcome)rng.Next(1, 6),
                Summary = "General inquiry — bill, balance, or address change request."
            });
        }
        ctx.CallLogs.AddRange(logs);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedOutageNotificationsAsync(ApplicationDbContext ctx)
    {
        if (await ctx.OutageNotifications.AnyAsync()) return;

        var outages = await ctx.Outages.AsNoTracking().ToListAsync();
        var schedules = await ctx.MaintenanceSchedules.AsNoTracking().Where(m => m.CustomersNotified).ToListAsync();
        var accounts = await ctx.ServiceAccounts.AsNoTracking().Include(a => a.Customer).ToListAsync();
        if (accounts.Count == 0) return;

        var rng = new Random(Phase06Seed + 9);
        var notifs = new List<OutageNotification>();

        // For each outage, notify a sample of affected accounts.
        foreach (var o in outages)
        {
            var affected = accounts.Where(a => a.ServiceTypeId == o.ServiceTypeId && (o.RegionId == null || a.ServicePoint?.RegionId == o.RegionId)).ToList();
            if (affected.Count == 0) affected = accounts.Where(a => a.ServiceTypeId == o.ServiceTypeId).Take(20).ToList();
            int n = Math.Min(affected.Count, rng.Next(8, 30));
            foreach (var a in affected.OrderBy(_ => rng.Next()).Take(n))
            {
                var statusRoll = rng.NextDouble();
                var status = statusRoll < 0.05 ? NotificationStatus.Failed
                           : statusRoll < 0.10 ? NotificationStatus.Pending
                           : statusRoll < 0.40 ? NotificationStatus.Sent
                           : statusRoll < 0.80 ? NotificationStatus.Delivered
                           : NotificationStatus.Read;
                var sent = o.StartedAt.AddMinutes(-rng.Next(0, 45));
                notifs.Add(new OutageNotification
                {
                    OutageId = o.Id,
                    CustomerId = a.CustomerId,
                    ServiceAccountId = a.Id,
                    Channel = rng.NextDouble() < 0.7 ? ContactChannel.Sms : ContactChannel.Whatsapp,
                    SentToPhone = a.Customer?.Phone,
                    SentAt = sent,
                    DeliveredAt = status >= NotificationStatus.Delivered ? sent.AddSeconds(rng.Next(5, 120)) : null,
                    ReadAt = status == NotificationStatus.Read ? sent.AddMinutes(rng.Next(2, 120)) : null,
                    Status = status,
                    MessageEn = $"Service interruption notice: {o.TitleEn ?? o.OutageNumber}. Expected restoration: see SMS for ETA.",
                    MessageAr = $"إشعار انقطاع: {o.TitleAr ?? o.OutageNumber}. وقت الاستعادة المتوقع موضح في الرسالة."
                });
            }
        }

        // Maintenance pre-notifications.
        foreach (var m in schedules)
        {
            var affected = accounts.Where(a => m.RegionId == null || a.ServicePoint?.RegionId == m.RegionId).Take(rng.Next(5, 20)).ToList();
            foreach (var a in affected)
            {
                var sent = m.ScheduledStart.AddDays(-rng.Next(1, 4));
                notifs.Add(new OutageNotification
                {
                    MaintenanceScheduleId = m.Id,
                    CustomerId = a.CustomerId,
                    ServiceAccountId = a.Id,
                    Channel = ContactChannel.Sms,
                    SentToPhone = a.Customer?.Phone,
                    SentAt = sent,
                    DeliveredAt = sent.AddSeconds(rng.Next(5, 90)),
                    Status = NotificationStatus.Delivered,
                    MessageEn = $"Planned maintenance notice: {m.TitleEn}. Window: {m.ScheduledStart:g} – {m.ScheduledEnd:g}.",
                    MessageAr = $"إشعار صيانة مجدولة: {m.TitleAr}. الفترة: {m.ScheduledStart:g} – {m.ScheduledEnd:g}."
                });
            }
        }

        ctx.OutageNotifications.AddRange(notifs);
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedSlaPoliciesAsync(ApplicationDbContext ctx)
    {
        if (await ctx.SlaPolicies.AnyAsync()) return;

        var segments = await ctx.CustomerSegments.AsNoTracking().ToListAsync();
        var services = await ctx.ServiceTypes.AsNoTracking().ToListAsync();
        var priorities = await ctx.TicketPriorities.AsNoTracking().ToListAsync();
        if (segments.Count == 0 || services.Count == 0 || priorities.Count == 0) return;

        var policies = new List<SlaPolicy>();
        // A representative grid — not every cell of the cube. Just the meaningful policies.
        var rows = new (string SegCode, string SvcCode, string PriName, int Frm, int Res)[]
        {
            // Residential
            (CustomerSegmentCodes.Residential, ServiceTypeCodes.Electricity, "High",     60,  480),
            (CustomerSegmentCodes.Residential, ServiceTypeCodes.Electricity, "Medium",   120, 1440),
            (CustomerSegmentCodes.Residential, ServiceTypeCodes.Water,       "High",     90,  720),
            (CustomerSegmentCodes.Residential, ServiceTypeCodes.Internet,    "Medium",   180, 2880),
            // Commercial — tighter
            (CustomerSegmentCodes.Commercial,  ServiceTypeCodes.Electricity, "High",     30,  240),
            (CustomerSegmentCodes.Commercial,  ServiceTypeCodes.Internet,    "High",     30,  240),
            // Industrial — tightest
            (CustomerSegmentCodes.Industrial,  ServiceTypeCodes.Electricity, "Critical", 15,  120),
            (CustomerSegmentCodes.Industrial,  ServiceTypeCodes.Gas,         "Critical", 15,  180),
            // Government — high segment, varies
            (CustomerSegmentCodes.Government,  ServiceTypeCodes.Electricity, "High",     30,  240),
            // Displaced — same response targets, distinct policy so it's reportable
            (CustomerSegmentCodes.Displaced,   ServiceTypeCodes.Electricity, "High",     60,  480),
            (CustomerSegmentCodes.Displaced,   ServiceTypeCodes.Water,       "High",     90,  720),
        };

        foreach (var r in rows)
        {
            var seg = segments.FirstOrDefault(s => s.Code == r.SegCode);
            var svc = services.FirstOrDefault(s => s.Code == r.SvcCode);
            var pri = priorities.FirstOrDefault(p => p.Name == r.PriName);
            if (seg is null || svc is null || pri is null) continue;
            policies.Add(new SlaPolicy
            {
                PolicyCode = $"SLA-{seg.Code.ToUpperInvariant().Substring(0, Math.Min(3, seg.Code.Length))}-{svc.Code.ToUpperInvariant().Substring(0, Math.Min(3, svc.Code.Length))}-{r.PriName.Substring(0, Math.Min(3, r.PriName.Length))}",
                NameEn = $"{seg.NameEn} {svc.NameEn} — {r.PriName} priority",
                NameAr = $"{seg.NameAr} {svc.NameAr} — أولوية {r.PriName}",
                CustomerSegmentId = seg.Id,
                ServiceTypeId = svc.Id,
                PriorityId = pri.Id,
                FirstResponseMinutes = r.Frm,
                ResolutionMinutes = r.Res,
                BusinessHoursOnly = seg.Code == CustomerSegmentCodes.Residential,
                EffectiveFrom = DateTime.UtcNow.AddYears(-1),
                IsActive = true,
                Notes = "Auto-seeded baseline policy. Refine via /SlaPolicies/Edit."
            });
        }
        ctx.SlaPolicies.AddRange(policies);
        await ctx.SaveChangesAsync();
    }
}
