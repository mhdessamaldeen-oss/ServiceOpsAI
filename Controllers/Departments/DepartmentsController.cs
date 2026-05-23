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

namespace ServiceOpsAI.Controllers.Departments;

[Authorize(Roles = RoleNames.Admin)]
public class DepartmentsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public DepartmentsController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();

        var query = _context.Departments.AsNoTracking()
            .Include(d => d.Region)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(d => d.NameEn.Contains(s)
                                  || d.NameAr.Contains(s)
                                  || (d.Region != null && d.Region.NameEn.Contains(s)));
        }

        query = query.OrderBy(d => d.Region != null ? d.Region.NameEn : string.Empty)
                     .ThenBy(d => d.ServiceType);

        var total = await query.CountAsync();
        var effectivePageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ProjectTo<DepartmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync();

        return View(new PagedResult<DepartmentDto>
        {
            Items = items,
            TotalCount = total,
            PageNumber = request.PageNumber,
            PageSize = effectivePageSize,
            Request = request,
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var d = await _context.Departments.AsNoTracking()
            .Include(x => x.Region)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound();
        return View(d);
    }

    public async Task<IActionResult> Create()
    {
        await PopulateGovernoratesAsync();
        return View(new Department());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("NameEn,NameAr,ServiceType,RegionId,ManagerUserId,ContactPhone,ContactEmail,IsActive")] Department department)
    {
        if (ModelState.IsValid)
        {
            _context.Departments.Add(department);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateGovernoratesAsync();
        return View(department);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var d = await _context.Departments.FindAsync(id);
        if (d is null) return NotFound();
        await PopulateGovernoratesAsync();
        return View(d);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,NameEn,NameAr,ServiceType,RegionId,ManagerUserId,ContactPhone,ContactEmail,IsActive")] Department department)
    {
        if (id != department.Id) return NotFound();
        if (ModelState.IsValid)
        {
            try { _context.Update(department); await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!await DepartmentExists(id)) return NotFound(); throw; }
            return RedirectToAction(nameof(Index));
        }
        await PopulateGovernoratesAsync();
        return View(department);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var d = await _context.Departments.FindAsync(id);
        if (d is not null)
        {
            var hasBills = await _context.Bills.AnyAsync(b => b.DepartmentId == id);
            var hasTickets = await _context.Tickets.AnyAsync(t => t.DepartmentId == id);
            if (hasBills || hasTickets)
            {
                TempData["Error"] = "Cannot delete this department because it has bills or tickets. Set IsActive = false instead.";
                return RedirectToAction(nameof(Index));
            }
            _context.Departments.Remove(d);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> DepartmentExists(int id) => await _context.Departments.AnyAsync(d => d.Id == id);

    private async Task PopulateGovernoratesAsync()
    {
        var govs = await _context.Regions
            .Where(r => r.RegionType == RegionType.Governorate)
            .OrderBy(r => r.NameEn)
            .Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr })
            .ToListAsync();
        ViewBag.Regions = new SelectList(govs, "Id", "Display");
    }
}
