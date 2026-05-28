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
public class OutageNotificationsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public OutageNotificationsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.OutageNotifications.AsNoTracking().Include(n => n.Outage).Include(n => n.Customer).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(n => (n.Customer != null && (n.Customer.FullNameEn.Contains(s) || n.Customer.FullNameAr.Contains(s)))
                                  || (n.Outage != null && n.Outage.OutageNumber.Contains(s))
                                  || (n.SentToPhone != null && n.SentToPhone.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "sent"        => query.OrderBy(n => n.SentAt),
            _             => query.OrderByDescending(n => n.SentAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<OutageNotificationDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<OutageNotificationDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var n = await _context.OutageNotifications.AsNoTracking().Include(x => x.Outage).Include(x => x.Customer).Include(x => x.MaintenanceSchedule).Include(x => x.ServiceAccount).FirstOrDefaultAsync(x => x.Id == id);
        return n is null ? NotFound() : View(n);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new OutageNotification { SentAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("OutageId,MaintenanceScheduleId,CustomerId,ServiceAccountId,Channel,SentToPhone,SentToEmail,SentAt,DeliveredAt,ReadAt,Status,MessageEn,MessageAr")] OutageNotification n)
    {
        if (ModelState.IsValid) { _context.OutageNotifications.Add(n); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(n);
    }

    public async Task<IActionResult> Edit(int id) { var n = await _context.OutageNotifications.FindAsync(id); if (n is null) return NotFound(); await PopulateRelatedAsync(); return View(n); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,OutageId,MaintenanceScheduleId,CustomerId,ServiceAccountId,Channel,SentToPhone,SentToEmail,SentAt,DeliveredAt,ReadAt,Status,MessageEn,MessageAr")] OutageNotification n)
    {
        if (id != n.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(n); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(n);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var n = await _context.OutageNotifications.FindAsync(id);
        if (n is null) return RedirectToAction(nameof(Index));
        _context.OutageNotifications.Remove(n); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Outages = new SelectList(await _context.Outages.OrderByDescending(o => o.StartedAt).Take(200).Select(o => new { o.Id, Display = o.OutageNumber }).ToListAsync(), "Id", "Display");
        ViewBag.Maintenances = new SelectList(await _context.MaintenanceSchedules.OrderByDescending(m => m.ScheduledStart).Take(200).Select(m => new { m.Id, Display = m.ScheduleNumber }).ToListAsync(), "Id", "Display");
        ViewBag.Customers = new SelectList(await _context.Customers.OrderBy(c => c.FullNameEn).Select(c => new { c.Id, Display = c.FullNameEn }).ToListAsync(), "Id", "Display");
    }
}
