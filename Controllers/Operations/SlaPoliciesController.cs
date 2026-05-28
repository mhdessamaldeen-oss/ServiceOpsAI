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
public class SlaPoliciesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public SlaPoliciesController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.SlaPolicies.AsNoTracking().Include(p => p.CustomerSegment).Include(p => p.ServiceType).Include(p => p.Priority).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(p => p.PolicyCode.Contains(s) || p.NameEn.Contains(s) || p.NameAr.Contains(s));
        }
        query = request.SortOrder switch
        {
            "response"  => query.OrderBy(p => p.FirstResponseMinutes),
            "resolution" => query.OrderBy(p => p.ResolutionMinutes),
            _           => query.OrderBy(p => p.NameEn)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<SlaPolicyDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<SlaPolicyDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await _context.SlaPolicies.AsNoTracking().Include(x => x.CustomerSegment).Include(x => x.ServiceType).Include(x => x.Priority).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : View(p);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new SlaPolicy { EffectiveFrom = DateTime.UtcNow, IsActive = true }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("PolicyCode,NameEn,NameAr,CustomerSegmentId,ServiceTypeId,PriorityId,FirstResponseMinutes,ResolutionMinutes,BusinessHoursOnly,EffectiveFrom,EffectiveTo,IsActive,Notes")] SlaPolicy p)
    {
        if (ModelState.IsValid) { _context.SlaPolicies.Add(p); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(p);
    }

    public async Task<IActionResult> Edit(int id) { var p = await _context.SlaPolicies.FindAsync(id); if (p is null) return NotFound(); await PopulateRelatedAsync(); return View(p); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,PolicyCode,NameEn,NameAr,CustomerSegmentId,ServiceTypeId,PriorityId,FirstResponseMinutes,ResolutionMinutes,BusinessHoursOnly,EffectiveFrom,EffectiveTo,IsActive,Notes")] SlaPolicy p)
    {
        if (id != p.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(p); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(p);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var p = await _context.SlaPolicies.FindAsync(id);
        if (p is null) return RedirectToAction(nameof(Index));
        _context.SlaPolicies.Remove(p); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Segments = new SelectList(await _context.CustomerSegments.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn + " / " + s.NameAr }).ToListAsync(), "Id", "Display");
        ViewBag.ServiceTypes = new SelectList(await _context.ServiceTypes.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Priorities = new SelectList(await _context.TicketPriorities.OrderBy(p => p.Level).Select(p => new { p.Id, Display = p.Name }).ToListAsync(), "Id", "Display");
    }
}
