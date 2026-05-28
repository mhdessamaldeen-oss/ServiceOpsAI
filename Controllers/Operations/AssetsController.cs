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

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class AssetsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public AssetsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.Assets.AsNoTracking().Include(a => a.ServiceType).Include(a => a.Region).Include(a => a.Department).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(a => a.AssetCode.Contains(s) || a.NameEn.Contains(s) || a.NameAr.Contains(s));
        }
        query = request.SortOrder switch
        {
            "code_desc"        => query.OrderByDescending(a => a.AssetCode),
            "commissioned"     => query.OrderBy(a => a.CommissionedAt),
            _                  => query.OrderBy(a => a.AssetCode)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<AssetDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<AssetDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await _context.Assets.AsNoTracking().Include(x => x.ServiceType).Include(x => x.Region).Include(x => x.Department).Include(x => x.ParentAsset).FirstOrDefaultAsync(x => x.Id == id);
        return a is null ? NotFound() : View(a);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new Asset { CommissionedAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("AssetCode,NameEn,NameAr,ServiceTypeId,RegionId,DepartmentId,AssetType,Status,CommissionedAt,DecommissionedAt,Specification,Latitude,Longitude,ParentAssetId")] Asset a)
    {
        if (ModelState.IsValid) { _context.Assets.Add(a); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(a);
    }

    public async Task<IActionResult> Edit(int id) { var a = await _context.Assets.FindAsync(id); if (a is null) return NotFound(); await PopulateRelatedAsync(); return View(a); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,AssetCode,NameEn,NameAr,ServiceTypeId,RegionId,DepartmentId,AssetType,Status,CommissionedAt,DecommissionedAt,Specification,Latitude,Longitude,ParentAssetId")] Asset a)
    {
        if (id != a.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(a); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(a);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var a = await _context.Assets.FindAsync(id);
        if (a is null) return RedirectToAction(nameof(Index));
        if (await _context.WorkOrders.AnyAsync(w => w.AssetId == id))
        { TempData["Error"] = "Asset has work orders; decommission instead."; return RedirectToAction(nameof(Index)); }
        _context.Assets.Remove(a); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.ServiceTypes = new SelectList(await _context.ServiceTypes.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Regions = new SelectList(await _context.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.NameEn).Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr }).ToListAsync(), "Id", "Display");
        ViewBag.Departments = new SelectList(await _context.Departments.OrderBy(d => d.NameEn).Select(d => new { d.Id, Display = d.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.ParentAssets = new SelectList(await _context.Assets.OrderBy(a => a.AssetCode).Select(a => new { a.Id, Display = a.AssetCode + " — " + a.NameEn }).ToListAsync(), "Id", "Display");
    }
}
