using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;

namespace ServiceOpsAI.Controllers.Billing;

[Authorize(Roles = RoleNames.Admin)]
public class TariffTiersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public TariffTiersController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.TariffTiers.AsNoTracking().Include(t => t.Tariff).ThenInclude(tr => tr!.ServiceType).Include(t => t.Tariff).ThenInclude(tr => tr!.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(t => (t.LabelEn != null && t.LabelEn.Contains(s)) || (t.LabelAr != null && t.LabelAr.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "tier_desc" => query.OrderByDescending(t => t.TierNumber),
            "rate"      => query.OrderBy(t => t.RatePerUnit),
            _           => query.OrderBy(t => t.TariffId).ThenBy(t => t.TierNumber)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<TariffTierDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<TariffTierDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var t = await _context.TariffTiers.AsNoTracking().Include(x => x.Tariff).ThenInclude(tr => tr!.ServiceType).Include(x => x.Tariff).ThenInclude(tr => tr!.Region).FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound() : View(t);
    }

    public async Task<IActionResult> Create() { await PopulateTariffsAsync(); return View(new TariffTier()); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("TariffId,TierNumber,FromUnit,ToUnit,RatePerUnit,LabelEn,LabelAr")] TariffTier t)
    {
        if (ModelState.IsValid) { _context.TariffTiers.Add(t); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateTariffsAsync(); return View(t);
    }

    public async Task<IActionResult> Edit(int id) { var t = await _context.TariffTiers.FindAsync(id); if (t is null) return NotFound(); await PopulateTariffsAsync(); return View(t); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,TariffId,TierNumber,FromUnit,ToUnit,RatePerUnit,LabelEn,LabelAr")] TariffTier t)
    {
        if (id != t.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(t); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateTariffsAsync(); return View(t);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var t = await _context.TariffTiers.FindAsync(id);
        if (t is null) return RedirectToAction(nameof(Index));
        _context.TariffTiers.Remove(t); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateTariffsAsync()
    {
        ViewBag.Tariffs = new SelectList(await _context.Tariffs.Include(t => t.ServiceType).Include(t => t.Region)
            .OrderBy(t => t.EffectiveFrom)
            .Select(t => new { t.Id, Display = (t.ServiceType != null ? t.ServiceType.NameEn : "?") + " / " + (t.Region != null ? t.Region.NameEn : "All") + " — " + t.EffectiveFrom.Year })
            .ToListAsync(), "Id", "Display");
    }
}
