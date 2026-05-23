using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class CountriesController : Controller
{
    private readonly ApplicationDbContext _context;
    public CountriesController(ApplicationDbContext context) { _context = context; }

    public async Task<IActionResult> Index()
    {
        var rows = await _context.Countries.AsNoTracking().OrderBy(c => c.NameEn).ToListAsync();
        return View(rows);
    }

    public async Task<IActionResult> Details(int id)
    {
        var row = await _context.Countries.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return NotFound();
        ViewBag.RegionCount = await _context.Regions.CountAsync(r => r.CountryId == id);
        return View(row);
    }

    public IActionResult Create() => View(new Country { IsActive = true });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("NameEn,NameAr,IsoCode,IsActive")] Country country)
    {
        if (ModelState.IsValid)
        {
            _context.Countries.Add(country);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        return View(country);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var row = await _context.Countries.FindAsync(id);
        if (row is null) return NotFound();
        return View(row);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,NameEn,NameAr,IsoCode,IsActive")] Country country)
    {
        if (id != country.Id) return NotFound();
        if (ModelState.IsValid)
        {
            try { _context.Update(country); await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!await CountryExists(id)) return NotFound(); throw; }
            return RedirectToAction(nameof(Index));
        }
        return View(country);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var row = await _context.Countries.FindAsync(id);
        if (row is not null)
        {
            var hasRegions = await _context.Regions.AnyAsync(r => r.CountryId == id);
            if (hasRegions)
            {
                TempData["Error"] = "Cannot delete this country because it has regions. Set IsActive=false instead.";
                return RedirectToAction(nameof(Index));
            }
            _context.Countries.Remove(row);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CountryExists(int id) => await _context.Countries.AnyAsync(c => c.Id == id);
}
