using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class OutagesController : Controller
{
    private readonly ApplicationDbContext _context;
    public OutagesController(ApplicationDbContext context) { _context = context; }

    public async Task<IActionResult> Index(int page = 1, int pageSize = 50)
    {
        var query = _context.Outages.AsNoTracking()
            .Include(o => o.Region)
            .Include(o => o.ServiceType)
            .Include(o => o.Department)
            .OrderByDescending(o => o.StartedAt);
        var total = await query.CountAsync();
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        ViewBag.Total = total;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        return View(rows);
    }
}
