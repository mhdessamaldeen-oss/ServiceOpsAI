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
public class PaymentMethodsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public PaymentMethodsController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.PaymentMethods.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(m => m.Code.Contains(s) || m.NameEn.Contains(s) || m.NameAr.Contains(s));
        }
        query = request.SortOrder switch
        {
            "name_desc" => query.OrderByDescending(m => m.NameEn),
            "fee"       => query.OrderBy(m => m.FeePercent),
            "fee_desc"  => query.OrderByDescending(m => m.FeePercent),
            _           => query.OrderBy(m => m.SortOrder).ThenBy(m => m.NameEn)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * pageSize)
            .Take(pageSize)
            .ProjectTo<PaymentMethodDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
        return View(new PagedResult<PaymentMethodDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var m = await _context.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return m is null ? NotFound() : View(m);
    }

    public IActionResult Create() => View(new PaymentMethod());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Code,NameEn,NameAr,IsDigital,FeePercent,SortOrder,IsActive")] PaymentMethod m)
    {
        if (ModelState.IsValid) { _context.PaymentMethods.Add(m); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        return View(m);
    }

    public async Task<IActionResult> Edit(int id) { var m = await _context.PaymentMethods.FindAsync(id); return m is null ? NotFound() : View(m); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Code,NameEn,NameAr,IsDigital,FeePercent,SortOrder,IsActive")] PaymentMethod m)
    {
        if (id != m.Id) return NotFound();
        if (ModelState.IsValid) { _context.Update(m); await _context.SaveChangesAsync(); return RedirectToAction(nameof(Index)); }
        return View(m);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var m = await _context.PaymentMethods.FindAsync(id);
        if (m is null) return RedirectToAction(nameof(Index));
        if (await _context.Payments.AnyAsync(p => p.PaymentMethodId == id))
        { TempData["Error"] = "Method is referenced by payments; deactivate instead."; return RedirectToAction(nameof(Index)); }
        _context.PaymentMethods.Remove(m); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
