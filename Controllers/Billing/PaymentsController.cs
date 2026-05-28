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
public class PaymentsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public PaymentsController(ApplicationDbContext context, IMapper mapper) { _context = context; _mapper = mapper; }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();
        var query = _context.Payments.AsNoTracking().Include(p => p.Bill).Include(p => p.PaymentMethod).Include(p => p.Currency).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(p => p.PaymentReference.Contains(s)
                                  || (p.Bill != null && p.Bill.BillNumber.Contains(s))
                                  || (p.ExternalTransactionId != null && p.ExternalTransactionId.Contains(s)));
        }
        query = request.SortOrder switch
        {
            "paid"      => query.OrderBy(p => p.PaidAt),
            "amount"    => query.OrderBy(p => p.AmountInBase),
            "amount_desc" => query.OrderByDescending(p => p.AmountInBase),
            _           => query.OrderByDescending(p => p.PaidAt)
        };
        var total = await query.CountAsync();
        var pageSize = request.GetEffectivePageSize(total);
        var items = await query.Skip((request.PageNumber - 1) * pageSize).Take(pageSize)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider).ToListAsync();
        return View(new PagedResult<PaymentDto> { Items = items, TotalCount = total, PageNumber = request.PageNumber, PageSize = pageSize, Request = request });
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await _context.Payments.AsNoTracking().Include(x => x.Bill).Include(x => x.ServiceAccount).Include(x => x.PaymentMethod).Include(x => x.Currency).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : View(p);
    }

    public async Task<IActionResult> Create() { await PopulateRelatedAsync(); return View(new Payment { PaidAt = DateTime.UtcNow }); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("PaymentReference,BillId,ServiceAccountId,PaymentMethodId,CurrencyId,Amount,ExchangeRateToBase,Status,PaidAt,ExternalTransactionId,Notes")] Payment p)
    {
        if (ModelState.IsValid)
        {
            // Compute base amount from captured exchange rate.
            p.AmountInBase = Math.Round(p.Amount * p.ExchangeRateToBase, 2);
            _context.Payments.Add(p);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateRelatedAsync(); return View(p);
    }

    public async Task<IActionResult> Edit(int id) { var p = await _context.Payments.FindAsync(id); if (p is null) return NotFound(); await PopulateRelatedAsync(); return View(p); }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,PaymentReference,BillId,ServiceAccountId,PaymentMethodId,CurrencyId,Amount,ExchangeRateToBase,Status,PaidAt,ExternalTransactionId,ReceivedByUserId,Notes")] Payment p)
    {
        if (id != p.Id) return NotFound();
        if (ModelState.IsValid)
        {
            p.AmountInBase = Math.Round(p.Amount * p.ExchangeRateToBase, 2);
            _context.Update(p); await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateRelatedAsync(); return View(p);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var p = await _context.Payments.FindAsync(id);
        if (p is null) return RedirectToAction(nameof(Index));
        _context.Payments.Remove(p); await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateRelatedAsync()
    {
        ViewBag.Bills = new SelectList(await _context.Bills.OrderByDescending(b => b.IssuedAt).Take(500).Select(b => new { b.Id, Display = b.BillNumber }).ToListAsync(), "Id", "Display");
        ViewBag.PaymentMethods = new SelectList(await _context.PaymentMethods.OrderBy(m => m.SortOrder).Select(m => new { m.Id, Display = m.NameEn + " / " + m.NameAr }).ToListAsync(), "Id", "Display");
        ViewBag.Currencies = new SelectList(await _context.Currencies.OrderByDescending(c => c.IsBase).ThenBy(c => c.Code).Select(c => new { c.Id, Display = c.Code + " — " + c.NameEn, c.ExchangeRateToBase }).ToListAsync(), "Id", "Display");
    }
}
