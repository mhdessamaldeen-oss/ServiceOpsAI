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
public class SubsidiesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public SubsidiesController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.Subsidies.AsNoTracking().Include(s => s.Bill).Include(s => s.Customer).Include(s => s.CustomerSegment).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(x => x.ProgramCode.Contains(s)
                                  || (x.Customer != null && (x.Customer.FullNameEn.Contains(s) || x.Customer.FullNameAr.Contains(s)))
                                  || (x.Bill != null && x.Bill.BillNumber.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "issued"      => query.OrderBy(x => x.IssuedAt),
            "amount"      => query.OrderBy(x => x.Amount),
            "amount_desc" => query.OrderByDescending(x => x.Amount),
            _             => query.OrderByDescending(x => x.IssuedAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<SubsidyDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<SubsidyDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var s = await _context.Subsidies.AsNoTracking().Include(x => x.Bill).Include(x => x.Customer).Include(x => x.CustomerSegment).FirstOrDefaultAsync(x => x.Id == id);
        return s is null ? NotFound() : View(s);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new Subsidy { IssuedAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("BillId,CustomerId,CustomerSegmentId,ProgramCode,ProgramNameEn,ProgramNameAr,Amount,AppliedPercent,IssuedAt,Status,Notes")] Subsidy s)
    {
        if (ModelState.IsValid) { _context.Subsidies.Add(s); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(s);
    }

    public async Task<IActionResult> Edit(int id) { var s = await _context.Subsidies.FindAsync(id); if (s is null) return NotFound(); await PopulateRelatedAsync(); return View(s); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,BillId,CustomerId,CustomerSegmentId,ProgramCode,ProgramNameEn,ProgramNameAr,Amount,AppliedPercent,IssuedAt,Status,Notes")] Subsidy s)
    {
        if (id != s.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(s); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        await PopulateRelatedAsync(); return View(s);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var s = await _context.Subsidies.FindAsync(id);
        if (s is null) return RedirectToAction(nameof(Index));
        _context.Subsidies.Remove(s); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Customers = new SelectList(await _context.Customers.OrderBy(c => c.FullNameEn).Select(c => new { c.Id, Display = c.FullNameEn }).ToListAsync(), "Id", "Display");
        ViewBag.Bills = new SelectList(await _context.Bills.OrderByDescending(b => b.IssuedAt).Take(500).Select(b => new { b.Id, Display = b.BillNumber }).ToListAsync(), "Id", "Display");
        ViewBag.Segments = new SelectList(await _context.CustomerSegments.OrderBy(s => s.SortOrder).Select(s => new { s.Id, Display = s.NameEn }).ToListAsync(), "Id", "Display");
    }
}
