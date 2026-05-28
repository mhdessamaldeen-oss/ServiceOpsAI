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
public class ServicePointsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public ServicePointsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.ServicePoints.AsNoTracking().Include(p => p.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(p => p.PointCode.Contains(s)
                                  || (p.AddressLineEn != null && p.AddressLineEn.Contains(s))
                                  || (p.AddressLineAr != null && p.AddressLineAr.Contains(s))
                                  || (p.MeterNumber  != null && p.MeterNumber.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "code_desc"     => query.OrderByDescending(p => p.PointCode),
            "installed"     => query.OrderBy(p => p.InstalledAt),
            "installed_desc" => query.OrderByDescending(p => p.InstalledAt),
            _               => query.OrderBy(p => p.PointCode)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<ServicePointDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<ServicePointDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await _context.ServicePoints.AsNoTracking().Include(x => x.Region).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : View(p);
    }

    public async Task<IActionResult> Create() { await PopulateRegionsAsync(); return View(new ServicePoint { InstalledAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("PointCode,RegionId,AddressLineEn,AddressLineAr,MeterNumber,Latitude,Longitude,PointType,InstalledAt,IsActive")] ServicePoint sp)
    {
        if (ModelState.IsValid) { _context.ServicePoints.Add(sp); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRegionsAsync(); return View(sp);
    }

    public async Task<IActionResult> Edit(int id) { var sp = await _context.ServicePoints.FindAsync(id); if (sp is null) return NotFound(); await PopulateRegionsAsync(); return View(sp); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,PointCode,RegionId,AddressLineEn,AddressLineAr,MeterNumber,Latitude,Longitude,PointType,InstalledAt,IsActive")] ServicePoint sp)
    {
        if (id != sp.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(sp); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRegionsAsync(); return View(sp);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var sp = await _context.ServicePoints.FindAsync(id);
        if (sp is null) return RedirectToAction(nameof(Index));
        if (await _context.ServiceAccounts.AnyAsync(a => a.ServicePointId == id))
        { TempData["Error"] = "Service point has active accounts; deactivate instead."; return RedirectToAction(nameof(Index)); }
        _context.ServicePoints.Remove(sp); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRegionsAsync()
    {
        var districts = await _context.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.NameEn)
            .Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr }).ToListAsync();
        ViewBag.Regions = new SelectList(districts, "Id", "Display");
    }
}
