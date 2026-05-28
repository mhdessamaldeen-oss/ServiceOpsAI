using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class CurrenciesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public CurrenciesController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.Currencies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(c => c.Code.Contains(s) || c.NameEn.Contains(s) || c.NameAr.Contains(s));
        }
        query = request.SortOrder switch
        {
            "code_desc" => query.OrderByDescending(c => c.Code),
            "rate"      => query.OrderBy(c => c.ExchangeRateToBase),
            "rate_desc" => query.OrderByDescending(c => c.ExchangeRateToBase),
            _           => query.OrderByDescending(c => c.IsBase).ThenBy(c => c.Code)
        };

        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * pageSize)
            .Take(pageSize)
            .ProjectTo<CurrencyDto>(_mapper.ConfigurationProvider)
            .ToListAsync();

        return View(new PagedResult<CurrencyDto>
        {
            Items = items, TotalCount = total, PageNumber = request.PageNumber,
            PageSize = pageSize, Request = request
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _context.Currencies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        return View(c);
    }

    public IActionResult Create() => View(new Currency { LastRateUpdate = DateTime.UtcNow });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Code,NameEn,NameAr,Symbol,IsBase,ExchangeRateToBase,LastRateUpdate,IsActive")] Currency currency)
    {
        if (ModelState.IsValid)
        {
            if (currency.IsBase)
            {
                // Only one base currency. Demote any existing base before saving.
                var existingBase = await _context.Currencies.Where(c => c.IsBase).ToListAsync();
                foreach (var b in existingBase) b.IsBase = false;
            }
            _context.Currencies.Add(currency);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(currency);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await _context.Currencies.FindAsync(id);
        if (c is null) return NotFound();
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,NameEn,NameAr,Symbol,IsBase,ExchangeRateToBase,LastRateUpdate,IsActive")] Currency currency)
    {
        if (id != currency.Id) return NotFound();
        if (ModelState.IsValid)
        {
            if (currency.IsBase)
            {
                var others = await _context.Currencies.Where(c => c.IsBase && c.Id != currency.Id).ToListAsync();
                foreach (var b in others) b.IsBase = false;
            }
            _context.Update(currency);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(currency);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var c = await _context.Currencies.FindAsync(id);
        if (c is null) return RedirectToAction(nameof(Index));
        if (c.IsBase) { TempData["Error"] = "Cannot delete the base currency."; return RedirectToAction(nameof(Index)); }
        var usedByPayments = await _context.Payments.AnyAsync(p => p.CurrencyId == id);
        if (usedByPayments) { TempData["Error"] = "Currency is referenced by payments; set to inactive instead."; return RedirectToAction(nameof(Index)); }
        _context.Currencies.Remove(c);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
