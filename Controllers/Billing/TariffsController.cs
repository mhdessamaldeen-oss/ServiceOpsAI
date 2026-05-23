using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;

namespace ServiceOpsAI.Controllers.Billing;

[Authorize(Roles = RoleNames.Admin)]
public class TariffsController : Controller
{
    private readonly ApplicationDbContext _context;
    public TariffsController(ApplicationDbContext context) { _context = context; }

    public async Task<IActionResult> Index()
    {
        var rows = await _context.Tariffs.AsNoTracking()
            .Include(t => t.ServiceType)
            .Include(t => t.Region)
            .OrderBy(t => t.ServiceType!.NameEn)
            .ThenBy(t => t.Region!.NameEn)
            .ThenByDescending(t => t.EffectiveFrom)
            .ToListAsync();
        return View(rows);
    }
}
