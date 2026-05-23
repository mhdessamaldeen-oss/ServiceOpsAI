using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using ServiceOpsAI.Models.DTOs;

namespace ServiceOpsAI.Controllers.ReferenceData
{
    [Authorize(Roles = RoleNames.Admin)]
    public class ReferenceDataController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public ReferenceDataController(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index()
        {
            ViewBag.Categories = await _context.TicketCategories.OrderBy(c => c.Name).ToListAsync();
            ViewBag.Priorities = await _context.TicketPriorities.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            ViewBag.Statuses = await _context.TicketStatuses.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            ViewBag.Sources = await _context.TicketSources.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            // New utility-domain lookups (Phase B):
            ViewBag.ServiceTypes    = await _context.ServiceTypes.OrderBy(s => s.SortOrder).ThenBy(s => s.NameEn).ToListAsync();
            ViewBag.ComplaintTypes  = await _context.ComplaintTypes.OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync();
            ViewBag.ResolutionTypes = await _context.ResolutionTypes.OrderBy(r => r.SortOrder).ThenBy(r => r.NameEn).ToListAsync();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> ToggleStatus(string type, int id)
        {
            switch (type.ToLowerInvariant())
            {
                case ReferenceDataTypes.Category:
                    var cat = await _context.TicketCategories.FindAsync(id);
                    if (cat != null) cat.IsActive = !cat.IsActive;
                    break;
                case ReferenceDataTypes.Priority:
                    var pri = await _context.TicketPriorities.FindAsync(id);
                    if (pri != null) pri.IsActive = !pri.IsActive;
                    break;
                case ReferenceDataTypes.Status:
                    var sts = await _context.TicketStatuses.FindAsync(id);
                    if (sts != null) sts.IsActive = !sts.IsActive;
                    break;
                case ReferenceDataTypes.Source:
                    var src = await _context.TicketSources.FindAsync(id);
                    if (src != null) src.IsActive = !src.IsActive;
                    break;
                case ReferenceDataTypes.ServiceType:
                    var svc = await _context.ServiceTypes.FindAsync(id);
                    if (svc != null) svc.IsActive = !svc.IsActive;
                    break;
                case ReferenceDataTypes.ComplaintType:
                    var ct = await _context.ComplaintTypes.FindAsync(id);
                    if (ct != null) ct.IsActive = !ct.IsActive;
                    break;
                case ReferenceDataTypes.ResolutionType:
                    var rt = await _context.ResolutionTypes.FindAsync(id);
                    if (rt != null) rt.IsActive = !rt.IsActive;
                    break;
            }
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddCategory(string name, string? nameAr)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = await _context.TicketCategories.FirstOrDefaultAsync(c => c.Name == name);
                if (existing != null) { existing.IsActive = true; if (!string.IsNullOrWhiteSpace(nameAr)) existing.NameAr = nameAr; }
                else { _context.TicketCategories.Add(new TicketCategory { Name = name, NameAr = nameAr ?? string.Empty, IsActive = true }); }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddPriority(string name, int level)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = await _context.TicketPriorities.FirstOrDefaultAsync(p => p.Name == name);
                if (existing != null) { existing.IsActive = true; existing.Level = level; }
                else { _context.TicketPriorities.Add(new TicketPriority { Name = name, Level = level, IsActive = true }); }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddStatus(string name, bool isClosedState)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = await _context.TicketStatuses.FirstOrDefaultAsync(s => s.Name == name);
                if (existing != null) { existing.IsActive = true; existing.IsClosedState = isClosedState; }
                else { _context.TicketStatuses.Add(new TicketStatus { Name = name, IsClosedState = isClosedState, IsActive = true }); }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddSource(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = await _context.TicketSources.FirstOrDefaultAsync(s => s.Name == name);
                if (existing != null) { existing.IsActive = true; }
                else { _context.TicketSources.Add(new TicketSource { Name = name, IsActive = true }); }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        // ─── New lookups (Phase B) ─────────────────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> AddServiceType(string code, string nameEn, string? nameAr, string? unit)
        {
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(nameEn))
            {
                var existing = await _context.ServiceTypes.FirstOrDefaultAsync(s => s.Code == code);
                if (existing != null)
                {
                    existing.IsActive = true;
                    existing.NameEn = nameEn;
                    if (!string.IsNullOrWhiteSpace(nameAr)) existing.NameAr = nameAr;
                    if (!string.IsNullOrWhiteSpace(unit))   existing.Unit   = unit;
                }
                else
                {
                    _context.ServiceTypes.Add(new ServiceType
                    {
                        Code = code,
                        NameEn = nameEn,
                        NameAr = nameAr ?? string.Empty,
                        Unit = unit,
                        IsActive = true,
                        SortOrder = (await _context.ServiceTypes.MaxAsync(s => (int?)s.SortOrder) ?? 0) + 10,
                    });
                }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddComplaintType(string code, string nameEn, string? nameAr)
        {
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(nameEn))
            {
                var existing = await _context.ComplaintTypes.FirstOrDefaultAsync(c => c.Code == code);
                if (existing != null) { existing.IsActive = true; existing.NameEn = nameEn; if (!string.IsNullOrWhiteSpace(nameAr)) existing.NameAr = nameAr; }
                else
                {
                    _context.ComplaintTypes.Add(new ComplaintType
                    {
                        Code = code,
                        NameEn = nameEn,
                        NameAr = nameAr ?? string.Empty,
                        IsActive = true,
                        SortOrder = (await _context.ComplaintTypes.MaxAsync(c => (int?)c.SortOrder) ?? 0) + 10,
                    });
                }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddResolutionType(string code, string nameEn, string? nameAr)
        {
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(nameEn))
            {
                var existing = await _context.ResolutionTypes.FirstOrDefaultAsync(r => r.Code == code);
                if (existing != null) { existing.IsActive = true; existing.NameEn = nameEn; if (!string.IsNullOrWhiteSpace(nameAr)) existing.NameAr = nameAr; }
                else
                {
                    _context.ResolutionTypes.Add(new ResolutionType
                    {
                        Code = code,
                        NameEn = nameEn,
                        NameAr = nameAr ?? string.Empty,
                        IsActive = true,
                        SortOrder = (await _context.ResolutionTypes.MaxAsync(r => (int?)r.SortOrder) ?? 0) + 10,
                    });
                }
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
