using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;

namespace ServiceOpsAI.Controllers.Customers;

[Authorize(Roles = RoleNames.Admin)]
public class CustomersController : Controller
{
    private readonly ApplicationDbContext _context;

    public CustomersController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(int page = 1, int pageSize = 50, string? search = null)
    {
        var query = _context.Customers.AsNoTracking().Include(c => c.Region).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(c => c.FullNameEn.Contains(search)
                                  || c.FullNameAr.Contains(search)
                                  || c.NationalId.Contains(search)
                                  || (c.Phone != null && c.Phone.Contains(search)));
        }
        var total = await query.CountAsync();
        var rows = await query.OrderBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        ViewBag.Total = total;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.Search = search;
        return View(rows);
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
            try
            {
                _context.Update(customer);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!await CustomerExists(id)) return NotFound();
                throw;
            }
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
            // Refuse delete if the customer has any bills or tickets — keeps referential integrity
            // and reflects how a real utility company handles "delete customer" requests.
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
        // Districts are the natural granularity for a customer's address.
        var districts = await _context.Regions
            .Where(r => r.RegionType == RegionType.District)
            .OrderBy(r => r.NameEn)
            .Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr })
            .ToListAsync();
        ViewBag.Regions = new SelectList(districts, "Id", "Display");
    }
}
