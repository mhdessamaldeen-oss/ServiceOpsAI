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

namespace ServiceOpsAI.Controllers.Bills;

[Authorize(Roles = RoleNames.Admin)]
public class BillsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public BillsController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request, string? status = null, string? service = null)
    {
        request.Normalize();

        var query = _context.Bills.AsNoTracking()
            .Include(b => b.Customer)
            .Include(b => b.Department)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(b => b.BillNumber.Contains(s)
                                  || (b.Customer != null && b.Customer.FullNameEn.Contains(s))
                                  || (b.Customer != null && b.Customer.NationalId.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BillStatus>(status, true, out var st))
            query = query.Where(b => b.Status == st);
        if (!string.IsNullOrWhiteSpace(service))
        {
            // service is now a ServiceType.Code (string) — filter by joining to the lookup row.
            var svcId = await _context.ServiceTypes
                .Where(s => s.Code == service)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync();
            if (svcId.HasValue) query = query.Where(b => b.ServiceTypeId == svcId.Value);
        }

        // Pass active service types to the view so the filter dropdown can render Code values.
        ViewBag.ServiceTypes = await _context.ServiceTypes
            .Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.NameEn)
            .ToListAsync();

        query = query.OrderByDescending(b => b.PeriodStart);

        var total = await query.CountAsync();
        var effectivePageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ProjectTo<BillDto>(_mapper.ConfigurationProvider)
            .ToListAsync();

        ViewBag.Status = status;
        ViewBag.Service = service;

        return View(new PagedResult<BillDto>
        {
            Items = items,
            TotalCount = total,
            PageNumber = request.PageNumber,
            PageSize = effectivePageSize,
            Request = request,
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var b = await _context.Bills.AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Department)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (b is null) return NotFound();
        return View(b);
    }

    public async Task<IActionResult> Create()
    {
        await PopulateDropdownsAsync();
        return View(new Bill
        {
            IssuedAt = DateTime.UtcNow,
            PeriodStart = DateTime.UtcNow.Date,
            PeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
            DueDate = DateTime.UtcNow.AddDays(30)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("BillNumber,CustomerId,DepartmentId,ServiceTypeId,PeriodStart,PeriodEnd,BaseAmount,UsageAmount,Taxes,TotalAmount,UsageQuantity,UsageUnit,Status,IssuedAt,DueDate,PaidAt,PaymentMethod,Notes")] Bill bill)
    {
        if (ModelState.IsValid)
        {
            _context.Bills.Add(bill);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateDropdownsAsync();
        return View(bill);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var b = await _context.Bills.FindAsync(id);
        if (b is null) return NotFound();
        await PopulateDropdownsAsync();
        return View(b);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,BillNumber,CustomerId,DepartmentId,ServiceTypeId,PeriodStart,PeriodEnd,BaseAmount,UsageAmount,Taxes,TotalAmount,UsageQuantity,UsageUnit,Status,IssuedAt,DueDate,PaidAt,PaymentMethod,Notes")] Bill bill)
    {
        if (id != bill.Id) return NotFound();
        if (ModelState.IsValid)
        {
            try { _context.Update(bill); await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!await BillExists(id)) return NotFound(); throw; }
            return RedirectToAction(nameof(Index));
        }
        await PopulateDropdownsAsync();
        return View(bill);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var b = await _context.Bills.FindAsync(id);
        if (b is not null)
        {
            var hasTickets = await _context.Tickets.AnyAsync(t => t.RelatedBillId == id);
            if (hasTickets)
            {
                TempData["Error"] = "Cannot delete this bill — a ticket references it. Resolve the ticket first.";
                return RedirectToAction(nameof(Index));
            }
            _context.Bills.Remove(b);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> BillExists(int id) => await _context.Bills.AnyAsync(b => b.Id == id);

    private async Task PopulateDropdownsAsync()
    {
        var customers = await _context.Customers.OrderBy(c => c.FullNameEn).Take(500)
            .Select(c => new { c.Id, Display = c.FullNameEn + " (" + c.NationalId + ")" }).ToListAsync();
        var depts = await _context.Departments.Include(d => d.Region).OrderBy(d => d.NameEn)
            .Select(d => new { d.Id, Display = d.NameEn }).ToListAsync();
        var serviceTypes = await _context.ServiceTypes.Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.NameEn)
            .Select(s => new { s.Id, Display = s.NameEn + " / " + s.NameAr }).ToListAsync();
        ViewBag.Customers = new SelectList(customers, "Id", "Display");
        ViewBag.Departments = new SelectList(depts, "Id", "Display");
        ViewBag.ServiceTypes = new SelectList(serviceTypes, "Id", "Display");
    }
}
