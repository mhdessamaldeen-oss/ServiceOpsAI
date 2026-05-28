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
public class CallLogsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public CallLogsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.CallLogs.AsNoTracking().Include(c => c.Customer).Include(c => c.RelatedTicket).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(c => c.CallReference.Contains(s)
                                  || (c.CallerPhone != null && c.CallerPhone.Contains(s))
                                  || (c.Customer != null && (c.Customer.FullNameEn.Contains(s) || c.Customer.FullNameAr.Contains(s))));
        }
        query = request.SortOrder switch
        {
            "started"      => query.OrderBy(c => c.StartedAt),
            "duration"     => query.OrderByDescending(c => c.DurationSeconds),
            _              => query.OrderByDescending(c => c.StartedAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<CallLogDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<CallLogDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await _context.CallLogs.AsNoTracking().Include(x => x.Customer).Include(x => x.RelatedTicket).Include(x => x.RelatedOutage).FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound() : View(c);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new CallLog { StartedAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("CallReference,CustomerId,CallerPhone,Direction,Channel,StartedAt,EndedAt,DurationSeconds,Outcome,RelatedTicketId,RelatedOutageId,Summary")] CallLog c)
    {
        if (ModelState.IsValid) { _context.CallLogs.Add(c); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(c);
    }

    public async Task<IActionResult> Edit(int id) { var c = await _context.CallLogs.FindAsync(id); if (c is null) return NotFound(); await PopulateRelatedAsync(); return View(c); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,CallReference,CustomerId,CallerPhone,Direction,Channel,StartedAt,EndedAt,DurationSeconds,Outcome,RelatedTicketId,RelatedOutageId,Summary")] CallLog c)
    {
        if (id != c.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(c); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(c);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var c = await _context.CallLogs.FindAsync(id);
        if (c is null) return RedirectToAction(nameof(Index));
        _context.CallLogs.Remove(c); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Customers = new SelectList(await _context.Customers.OrderBy(c => c.FullNameEn).Select(c => new { c.Id, Display = c.FullNameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Tickets = new SelectList(await _context.Tickets.OrderByDescending(t => t.CreatedAt).Take(300).Select(t => new { t.Id, Display = t.TicketNumber }).ToListAsync(), "Id", "Display");
        ViewBag.Outages = new SelectList(await _context.Outages.OrderByDescending(o => o.StartedAt).Take(100).Select(o => new { o.Id, Display = o.OutageNumber }).ToListAsync(), "Id", "Display");
    }
}
