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
public class WorkOrdersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public WorkOrdersController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.WorkOrders.AsNoTracking()
            .Include(w => w.Ticket).Include(w => w.Outage).Include(w => w.Asset).Include(w => w.AssignedTechnician).Include(w => w.Department).Include(w => w.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(w => w.OrderNumber.Contains(s)
                                  || w.TitleEn.Contains(s)
                                  || (w.Ticket != null && w.Ticket.TicketNumber.Contains(s))
                                  || (w.Outage != null && w.Outage.OutageNumber.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "created"      => query.OrderBy(w => w.CreatedAt),
            "status"       => query.OrderBy(w => w.Status),
            "priority"     => query.OrderByDescending(w => w.Priority),
            _              => query.OrderByDescending(w => w.CreatedAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<WorkOrderDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<WorkOrderDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var w = await _context.WorkOrders.AsNoTracking()
            .Include(x => x.Ticket).Include(x => x.Outage).Include(x => x.Asset).Include(x => x.ServicePoint)
            .Include(x => x.AssignedTechnician).Include(x => x.Department).Include(x => x.Region)
            .FirstOrDefaultAsync(x => x.Id == id);
        return w is null ? NotFound() : View(w);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new WorkOrder { CreatedAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("OrderNumber,OrderType,Status,Priority,TicketId,OutageId,AssetId,ServicePointId,DepartmentId,RegionId,AssignedTechnicianId,CreatedAt,DispatchedAt,ArrivedOnSiteAt,CompletedAt,TitleEn,TitleAr,Description,ResolutionNotes,RequiredSecondVisit")] WorkOrder w)
    {
        if (ModelState.IsValid) { _context.WorkOrders.Add(w); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(w);
    }

    public async Task<IActionResult> Edit(int id) { var w = await _context.WorkOrders.FindAsync(id); if (w is null) return NotFound(); await PopulateRelatedAsync(); return View(w); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,OrderNumber,OrderType,Status,Priority,TicketId,OutageId,AssetId,ServicePointId,DepartmentId,RegionId,AssignedTechnicianId,CreatedAt,DispatchedAt,ArrivedOnSiteAt,CompletedAt,TitleEn,TitleAr,Description,ResolutionNotes,RequiredSecondVisit")] WorkOrder w)
    {
        if (id != w.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(w); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(w);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var w = await _context.WorkOrders.FindAsync(id);
        if (w is null) return RedirectToAction(nameof(Index));
        _context.WorkOrders.Remove(w); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Tickets = new SelectList(await _context.Tickets.OrderByDescending(t => t.CreatedAt).Take(500).Select(t => new { t.Id, Display = t.TicketNumber + " — " + t.Title }).ToListAsync(), "Id", "Display");
        ViewBag.Outages = new SelectList(await _context.Outages.OrderByDescending(o => o.StartedAt).Take(200).Select(o => new { o.Id, Display = o.OutageNumber }).ToListAsync(), "Id", "Display");
        ViewBag.Assets = new SelectList(await _context.Assets.OrderBy(a => a.AssetCode).Select(a => new { a.Id, Display = a.AssetCode + " — " + a.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Technicians = new SelectList(await _context.Technicians.Where(t => t.IsActive).OrderBy(t => t.FullNameEn).Select(t => new { t.Id, Display = t.EmployeeCode + " — " + t.FullNameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Departments = new SelectList(await _context.Departments.OrderBy(d => d.NameEn).Select(d => new { d.Id, Display = d.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Regions = new SelectList(await _context.Regions.Where(r => r.RegionType == RegionType.District).OrderBy(r => r.NameEn).Select(r => new { r.Id, Display = r.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.ServicePoints = new SelectList(await _context.ServicePoints.OrderBy(p => p.PointCode).Select(p => new { p.Id, Display = p.PointCode }).ToListAsync(), "Id", "Display");
    }
}
