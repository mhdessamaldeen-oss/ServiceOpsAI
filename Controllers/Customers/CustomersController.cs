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

namespace ServiceOpsAI.Controllers.Customers;

[Authorize(Roles = RoleNames.Admin)]
public class CustomersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IMapper _mapper;

    public CustomersController(ApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper = mapper;
    }

    public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
    {
        request.Normalize();

        var query = _context.Customers.AsNoTracking().Include(c => c.Region).AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchString))
        {
            var s = request.SearchString;
            query = query.Where(c => c.FullNameEn.Contains(s)
                                  || c.FullNameAr.Contains(s)
                                  || c.NationalId.Contains(s)
                                  || (c.Phone != null && c.Phone.Contains(s)));
        }

        query = request.SortOrder switch
        {
            "name_desc"    => query.OrderByDescending(c => c.FullNameEn),
            "region"       => query.OrderBy(c => c.Region != null ? c.Region.NameEn : string.Empty),
            "region_desc"  => query.OrderByDescending(c => c.Region != null ? c.Region.NameEn : string.Empty),
            "status"       => query.OrderBy(c => c.Status),
            "status_desc"  => query.OrderByDescending(c => c.Status),
            "signup_desc"  => query.OrderByDescending(c => c.SignupAt),
            "signup"       => query.OrderBy(c => c.SignupAt),
            _              => query.OrderBy(c => c.FullNameEn),
        };

        var total = await query.CountAsync();
        var effectivePageSize = request.GetEffectivePageSize(total);
        var items = await query
            .Skip((request.PageNumber - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ProjectTo<CustomerDto>(_mapper.ConfigurationProvider)
            .ToListAsync();

        return View(new PagedResult<CustomerDto>
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
        var customer = await _context.Customers.AsNoTracking()
            .Include(c => c.Region)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null) return NotFound();
        return View(customer);
    }

    public async Task<IActionResult> Create()
    {
        await PopulateRegionsAsync();
        return View(new Customer { SignupAt = DateTime.UtcNow });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("FullNameEn,FullNameAr,NationalId,Email,Phone,RegionId,AddressLineEn,AddressLineAr,Status,SignupAt,Notes")] Customer customer)
    {
        if (ModelState.IsValid)
        {
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateRegionsAsync();
        return View(customer);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer is null) return NotFound();
        await PopulateRegionsAsync();
        return View(customer);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,FullNameEn,FullNameAr,NationalId,Email,Phone,RegionId,AddressLineEn,AddressLineAr,Status,SignupAt,ChurnedAt,Notes")] Customer customer)
    {
        if (id != customer.Id) return NotFound();
        if (ModelState.IsValid)
        {
            try { _context.Update(customer); await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!await CustomerExists(id)) return NotFound(); throw; }
            return RedirectToAction(nameof(Index));
        }
        await PopulateRegionsAsync();
        return View(customer);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer is not null)
        {
            var hasBills = await _context.Bills.AnyAsync(b => b.CustomerId == id);
            var hasTickets = await _context.Tickets.AnyAsync(t => t.CustomerId == id);
            if (hasBills || hasTickets)
            {
                TempData["Error"] = "Cannot delete this customer because they have bills or tickets. Set Status to Churned instead.";
                return RedirectToAction(nameof(Index));
            }
            _context.Customers.Remove(customer);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CustomerExists(int id) => await _context.Customers.AnyAsync(c => c.Id == id);

    private async Task PopulateRegionsAsync()
    {
        var districts = await _context.Regions
            .Where(r => r.RegionType == RegionType.District)
            .OrderBy(r => r.NameEn)
            .Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr })
            .ToListAsync();
        ViewBag.Regions = new SelectList(districts, "Id", "Display");
    }
}
