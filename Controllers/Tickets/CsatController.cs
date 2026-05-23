using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;

namespace ServiceOpsAI.Controllers.Tickets;

[Authorize(Roles = RoleNames.Admin)]
public class CsatController : Controller
{
    private readonly ApplicationDbContext _context;
    public CsatController(ApplicationDbContext context) { _context = context; }

    public async Task<IActionResult> Index(int page = 1, int pageSize = 50)
    {
        var query = _context.CsatResponses.AsNoTracking()
            .Include(c => c.Ticket).ThenInclude(t => t!.Customer)
            .OrderByDescending(c => c.RespondedAt);
        var total = await query.CountAsync();
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        ViewBag.Total = total;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.AvgScore = await _context.CsatResponses.AverageAsync(c => (double?)c.Score) ?? 0;
        return View(rows);
    }
}
