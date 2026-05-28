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
public class MaintenanceSchedulesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public MaintenanceSchedulesController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.MaintenanceSchedules.AsNoTracking().Include(m => m.Asset).Include(m => m.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(m => m.ScheduleNumber.Contains(s) || m.TitleEn.Contains(s));
        }
        query = request.SortOrder switch
        {
            "start_desc"  => query.OrderByDescending(m => m.ScheduledStart),
            "status"      => query.OrderBy(m => m.Status),
            _             => query.OrderBy(m => m.ScheduledStart)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<MaintenanceScheduleDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<MaintenanceScheduleDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var m = await _context.MaintenanceSchedules.AsNoTracking().Include(x => x.Asset).Include(x => x.Region).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id);
        return m is null ? NotFound() : View(m);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new MaintenanceSchedule { ScheduledStart = DateTime.UtcNow.AddDays(7), ScheduledEnd = DateTime.UtcNow.AddDays(7).AddHours(4) }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("ScheduleNumber,AssetId,RegionId,DepartmentId,ScheduledStart,ScheduledEnd,ActualStart,ActualEnd,Status,MaintenanceType,TitleEn,TitleAr,Description,ExpectedAffectedCustomers,CustomersNotified")] MaintenanceSchedule m)
    {
        if (ModelState.IsValid) { _context.MaintenanceSchedules.Add(m); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(m);
    }

    public async Task<IActionResult> Edit(int id) { var m = await _context.MaintenanceSchedules.FindAsync(id); if (m is null) return NotFound(); await PopulateRelatedAsync(); return View(m); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,ScheduleNumber,AssetId,RegionId,DepartmentId,ScheduledStart,ScheduledEnd,ActualStart,ActualEnd,Status,MaintenanceType,TitleEn,TitleAr,Description,ExpectedAffectedCustomers,CustomersNotified")] MaintenanceSchedule m)
    {
        if (id != m.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(m); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(m);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var m = await _context.MaintenanceSchedules.FindAsync(id);
        if (m is null) return RedirectToAction(nameof(Index));
        _context.MaintenanceSchedules.Remove(m); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Assets = new SelectList(await _context.Assets.OrderBy(a => a.AssetCode).Select(a => new { a.Id, Display = a.AssetCode + " — " + a.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Regions = new SelectList(await _context.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.NameEn).Select(r => new { r.Id, Display = r.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Departments = new SelectList(await _context.Departments.OrderBy(d => d.NameEn).Select(d => new { d.Id, Display = d.NameEn }).ToListAsync(), "Id", "Display");
    }
}
