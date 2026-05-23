using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class MeterReadingsController : Controller
{
    private readonly ApplicationDbContext _context;
    public MeterReadingsController(ApplicationDbContext context) { _context = context; }

    public async Task<IActionResult> Index(int page = 1, int pageSize = 50, int? customerId = null)
    {
        var query = _context.MeterReadings.AsNoTracking()
            .Include(m => m.Customer)
            .Include(m => m.ServiceType)
            .OrderByDescending(m => m.ReadingDate).AsQueryable();
        if (customerId.HasValue) query = query.Where(m => m.CustomerId == customerId.Value);
        var total = await query.CountAsync();
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        ViewBag.Total = total;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        return View(rows);
    }
}
