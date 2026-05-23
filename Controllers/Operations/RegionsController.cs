using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;

namespace ServiceOpsAI.Controllers.Operations;

[Authorize(Roles = RoleNames.Admin)]
public class RegionsController : Controller
{
    private readonly ApplicationDbContext _context;
    public RegionsController(ApplicationDbContext context) { _context = context; }

    // Hierarchical Index: governorates with their child districts grouped underneath.
    public async Task<IActionResult> Index(int? countryId = null)
    {
        var query = _context.Regions.AsNoTracking().Include(r => r.Country).Include(r => r.ParentRegion).AsQueryable();
        if (countryId.HasValue) query = query.Where(r => r.CountryId == countryId.Value);

        var allRegions = await query.OrderBy(r => r.NameEn).ToListAsync();

        ViewBag.Countries = await _context.Countries.OrderBy(c => c.NameEn).ToListAsync();
        ViewBag.SelectedCountryId = countryId;
        return View(allRegions);
    }

    public async Task<IActionResult> Details(int id)
    {
        var row = await _context.Regions.AsNoTracking()
            .Include(r => r.Country)
            .Include(r => r.ParentRegion)
            .Include(r => r.ChildRegions)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return NotFound();
        return View(row);
    }

    public async Task<IActionResult> Create(int? parentRegionId = null)
    {
        await PopulateDropdownsAsync(parentRegionId);
        return View(new Region
        {
            IsActive = true,
            ParentRegionId = parentRegionId,
            RegionType = parentRegionId.HasValue ? RegionType.District : RegionType.Governorate,
            CountryId = await _context.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("NameEn,NameAr,CountryId,ParentRegionId,RegionType,IsActive")] Region region)
    {
        if (ModelState.IsValid)
        {
            _context.Regions.Add(region);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        await PopulateDropdownsAsync(region.ParentRegionId);
        return View(region);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var row = await _context.Regions.FindAsync(id);
        if (row is null) return NotFound();
        await PopulateDropdownsAsync(row.ParentRegionId);
        return View(row);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,NameEn,NameAr,CountryId,ParentRegionId,RegionType,IsActive")] Region region)
    {
        if (id != region.Id) return NotFound();
        if (ModelState.IsValid)
        {
            try { _context.Update(region); await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!await RegionExists(id)) return NotFound(); throw; }
            return RedirectToAction(nameof(Index));
        }
        await PopulateDropdownsAsync(region.ParentRegionId);
        return View(region);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var row = await _context.Regions.FindAsync(id);
        if (row is not null)
        {
            var hasChildren = await _context.Regions.AnyAsync(r => r.ParentRegionId == id);
            var hasCustomers = await _context.Customers.AnyAsync(c => c.RegionId == id);
            var hasDepartments = await _context.Departments.AnyAsync(d => d.RegionId == id);
            if (hasChildren || hasCustomers || hasDepartments)
            {
                TempData["Error"] = "Cannot delete: this region is referenced by child regions, customers, or departments. Set IsActive=false instead.";
                return RedirectToAction(nameof(Index));
            }
            _context.Regions.Remove(row);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> RegionExists(int id) => await _context.Regions.AnyAsync(r => r.Id == id);

    private async Task PopulateDropdownsAsync(int? selectedParent = null)
    {
        var countries = await _context.Countries.OrderBy(c => c.NameEn).Select(c => new { c.Id, Display = c.NameEn + " / " + c.NameAr }).ToListAsync();
        ViewBag.Countries = new SelectList(countries, "Id", "Display");

        var parents = await _context.Regions
            .Where(r => r.RegionType == RegionType.Governorate)
            .OrderBy(r => r.NameEn)
            .Select(r => new { r.Id, Display = r.NameEn + " / " + r.NameAr })
            .ToListAsync();
        ViewBag.ParentRegions = new SelectList(parents, "Id", "Display", selectedParent);
    }
}
