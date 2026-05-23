using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;
using AutoMapper;
using AutoMapper.QueryableExtensions;

namespace ServiceOpsAI.Controllers.Entities
{
    [Authorize(Roles = RoleNames.Admin)]
    public class EntitiesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public EntitiesController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
        {
            request.Normalize();

            var entities = _context.Departments
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrEmpty(request.SearchString))
            {
                entities = entities.Where(e => e.Name.Contains(request.SearchString));
            }

            switch (request.SortOrder)
            {
                case "name_desc":
                    entities = entities.OrderByDescending(e => e.Name);
                    break;
                case "Status":
                    entities = entities.OrderBy(e => e.IsActive);
                    break;
                case "status_desc":
                    entities = entities.OrderByDescending(e => e.IsActive);
                    break;
                default:
                    entities = entities.OrderBy(e => e.Name);
                    break;
            }

            var totalCount = await entities.CountAsync();
            var effectivePageSize = request.GetEffectivePageSize(totalCount);
            var items = await entities.Skip((request.PageNumber - 1) * effectivePageSize)
                                      .Take(effectivePageSize)
                                      .ProjectTo<DepartmentDto>(_mapper.ConfigurationProvider)
                                      .ToListAsync();

            var pagedResult = new PagedResult<DepartmentDto>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };

            return View(pagedResult);
        }

        // GET: Entities/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Entities/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,Name,IsActive")] Department entity)
        {
            if (ModelState.IsValid)
            {
                _context.Add(entity);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(entity);
        }

        // GET: Entities/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var entity = await _context.Departments.FindAsync(id);
            if (entity == null) return NotFound();

            return View(entity);
        }

        // POST: Entities/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Name,IsActive")] Department entity)
        {
            if (id != entity.Id) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(entity);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DepartmentExists(entity.Id)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(entity);
        }

        // POST: Entities/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var entity = await _context.Departments.FindAsync(id);
            if (entity != null)
            {
                var hasUsers = await _context.Users.AnyAsync(u => u.DepartmentId == id);
                var hasTickets = await _context.Tickets.AnyAsync(t => t.DepartmentId == id);

                if (hasUsers || hasTickets)
                {
                    TempData["Error"] = "Cannot delete this entity because it has associated users or tickets. Consider editing it to set IsActive to false instead.";
                    return RedirectToAction(nameof(Index));
                }

                _context.Departments.Remove(entity);
                await _context.SaveChangesAsync();
            }
            
            return RedirectToAction(nameof(Index));
        }

        private bool DepartmentExists(int id)
        {
            return _context.Departments.Any(e => e.Id == id);
        }
    }
}
