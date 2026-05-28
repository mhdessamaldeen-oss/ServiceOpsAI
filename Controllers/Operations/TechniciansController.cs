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
public class TechniciansController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public TechniciansController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.Technicians.AsNoTracking().Include(t => t.Department).Include(t => t.PrimaryRegion).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(t => t.EmployeeCode.Contains(s) || t.FullNameEn.Contains(s) || t.FullNameAr.Contains(s) || (t.Phone != null && t.Phone.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "experience"  => query.OrderBy(t => t.YearsOfExperience),
            "experience_desc" => query.OrderByDescending(t => t.YearsOfExperience),
            _             => query.OrderBy(t => t.FullNameEn)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<TechnicianDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<TechnicianDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var t = await _context.Technicians.AsNoTracking().Include(x => x.Department).Include(x => x.PrimaryRegion).FirstOrDefaultAsync(x => x.Id == id);
        return t is null ? NotFound() : View(t);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new Technician { HiredAt = DateTime.UtcNow, IsActive = true }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("EmployeeCode,FullNameEn,FullNameAr,Phone,DepartmentId,PrimaryRegionId,Specialty,YearsOfExperience,UserId,HiredAt,IsActive")] Technician t)
    {
        if (ModelState.IsValid) { _context.Technicians.Add(t); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(t);
    }

    public async Task<IActionResult> Edit(int id) { var t = await _context.Technicians.FindAsync(id); if (t is null) return NotFound(); await PopulateRelatedAsync(); return View(t); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,EmployeeCode,FullNameEn,FullNameAr,Phone,DepartmentId,PrimaryRegionId,Specialty,YearsOfExperience,UserId,HiredAt,IsActive")] Technician t)
    {
        if (id != t.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(t); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(t);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var t = await _context.Technicians.FindAsync(id);
        if (t is null) return RedirectToAction(nameof(Index));
        if (await _context.WorkOrders.AnyAsync(w => w.AssignedTechnicianId == id))
        { TempData["Error"] = "Technician has work orders; set inactive instead."; return RedirectToAction(nameof(Index)); }
        _context.Technicians.Remove(t); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Departments = new SelectList(await _context.Departments.OrderBy(d => d.NameEn).Select(d => new { d.Id, Display = d.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Regions = new SelectList(await _context.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.NameEn).Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr }).ToListAsync(), "Id", "Display");
    }
}
