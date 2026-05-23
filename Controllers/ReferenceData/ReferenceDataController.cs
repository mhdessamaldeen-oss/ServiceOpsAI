using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using AISupportAnalysisPlatform.Models.DTOs;

namespace AISupportAnalysisPlatform.Controllers.ReferenceData
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
            ViewBag.Categories = await _context.TicketCategories.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            ViewBag.Priorities = await _context.TicketPriorities.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            ViewBag.Statuses = await _context.TicketStatuses.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
            ViewBag.Sources = await _context.TicketSources.ProjectTo<ReferenceDataDto>(_mapper.ConfigurationProvider).ToListAsync();
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
            }
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> AddCategory(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var existing = await _context.TicketCategories.FirstOrDefaultAsync(c => c.Name == name);
                if (existing != null) { existing.IsActive = true; }
                else { _context.TicketCategories.Add(new TicketCategory { Name = name, IsActive = true }); }
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
    }
}
