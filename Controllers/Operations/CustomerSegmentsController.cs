using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class CustomerSegmentsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public CustomerSegmentsController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.CustomerSegments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(c => c.Code.Contains(s) || c.NameEn.Contains(s) || c.NameAr.Contains(s));
        }
        query = request.SortOrder switch
        {
            "name_desc" => query.OrderByDescending(c => c.NameEn),
            _           => query.OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * pageSize)
            .Take(pageSize)
            .ProjectTo<CustomerSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
        return View(new PagedResult<CustomerSegmentDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var s = await _context.CustomerSegments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return s is null ? NotFound() : View(s);
    }

    public IActionResult Create() => View(new CustomerSegment());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Code,NameEn,NameAr,IsSubsidyEligible,DefaultPriorityFloor,SortOrder,IsActive")] CustomerSegment seg)
    {
        if (ModelState.IsValid) { _context.CustomerSegments.Add(seg); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        return View(seg);
    }

    public async Task<IActionResult> Edit(int id) { var s = await _context.CustomerSegments.FindAsync(id); return s is null ? NotFound() : View(s); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,NameEn,NameAr,IsSubsidyEligible,DefaultPriorityFloor,SortOrder,IsActive")] CustomerSegment seg)
    {
        if (id != seg.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(seg); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        return View(seg);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var s = await _context.CustomerSegments.FindAsync(id);
        if (s is null) return RedirectToAction(nameof(Index));
        if (await _context.ServiceAccounts.AnyAsync(a => a.CustomerSegmentId == id))
        { TempData["Error"] = "Segment is in use; deactivate instead."; return RedirectToAction(nameof(Index)); }
        _context.CustomerSegments.Remove(s); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
