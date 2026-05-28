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
public class ServiceAccountsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public ServiceAccountsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.ServiceAccounts.AsNoTracking()
            .Include(a => a.Customer).Include(a => a.ServiceType).Include(a => a.ServicePoint).Include(a => a.CustomerSegment).Include(a => a.Department).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(a => a.AccountNumber.Contains(s)
                                  || (a.Customer != null && (a.Customer.FullNameEn.Contains(s) || a.Customer.FullNameAr.Contains(s)))
                                  || (a.ServicePoint != null && a.ServicePoint.PointCode.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "activated"     => query.OrderBy(a => a.ActivatedAt),
            "activated_desc" => query.OrderByDescending(a => a.ActivatedAt),
            "status"        => query.OrderBy(a => a.Status),
            _               => query.OrderByDescending(a => a.ActivatedAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<ServiceAccountDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<ServiceAccountDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await _context.ServiceAccounts.AsNoTracking()
            .Include(x => x.Customer).Include(x => x.ServiceType).Include(x => x.ServicePoint).ThenInclude(p => p!.Region)
            .Include(x => x.CustomerSegment).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id);
        return a is null ? NotFound() : View(a);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new ServiceAccount { ActivatedAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("AccountNumber,CustomerId,ServiceTypeId,ServicePointId,CustomerSegmentId,DepartmentId,ActivatedAt,DeactivatedAt,Status,ContractedCapacity,CapacityUnit,Notes")] ServiceAccount acc)
    {
        if (ModelState.IsValid) { _context.ServiceAccounts.Add(acc); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(acc);
    }

    public async Task<IActionResult> Edit(int id) { var a = await _context.ServiceAccounts.FindAsync(id); if (a is null) return NotFound(); await PopulateRelatedAsync(); return View(a); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,AccountNumber,CustomerId,ServiceTypeId,ServicePointId,CustomerSegmentId,DepartmentId,ActivatedAt,DeactivatedAt,Status,ContractedCapacity,CapacityUnit,Notes")] ServiceAccount acc)
    {
        if (id != acc.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(acc); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(acc);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var a = await _context.ServiceAccounts.FindAsync(id);
        if (a is null) return RedirectToAction(nameof(Index));
        if (await _context.Payments.AnyAsync(p => p.ServiceAccountId == id))
        { TempData["Error"] = "Account has payments; terminate instead."; return RedirectToAction(nameof(Index)); }
        _context.ServiceAccounts.Remove(a); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Customers = new SelectList(await _context.Customers.OrderBy(c => c.FullNameEn).Select(c => new { c.Id, Display = c.FullNameEn + " / " + c.FullNameAr }).ToListAsync(), "Id", "Display");
        ViewBag.ServiceTypes = new SelectList(await _context.ServiceTypes.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn + " / " + s.NameAr }).ToListAsync(), "Id", "Display");
        ViewBag.ServicePoints = new SelectList(await _context.ServicePoints.OrderBy(p => p.PointCode).Select(p => new { p.Id, Display = p.PointCode + " — " + (p.AddressLineEn ?? "") }).ToListAsync(), "Id", "Display");
        ViewBag.CustomerSegments = new SelectList(await _context.CustomerSegments.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn + " / " + s.NameAr }).ToListAsync(), "Id", "Display");
        ViewBag.Departments = new SelectList(await _context.Departments.OrderBy(d => d.NameEn).Select(d => new { d.Id, Display = d.NameEn }).ToListAsync(), "Id", "Display");
    }
}
