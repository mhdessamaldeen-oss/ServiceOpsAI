using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Models;
using ServiceOpsAI.Models.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using ServiceOpsAI.Models.DTOs;
using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Controllers.Dashboard
{
    [Authorize]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IMapper _mapper;

        public DashboardController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IMapper mapper)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index()
        {
            var openStatusIds = await _context.TicketStatuses
                .AsNoTracking()
                .Where(s => !s.IsClosedState)
                .Select(s => s.Id)
                .ToListAsync();
            var resolvedStatusId = await _context.TicketStatuses
                .AsNoTracking()
                .Where(s => s.Name == TicketStatusNames.Resolved)
                .Select(s => s.Id)
                .FirstOrDefaultAsync();
            var closedStatusId = await _context.TicketStatuses
                .AsNoTracking()
                .Where(s => s.Name == TicketStatusNames.Closed)
                .Select(s => s.Id)
                .FirstOrDefaultAsync();

            var userId = _userManager.GetUserId(User);
            var user = await _userManager.FindByIdAsync(userId!);
            
            if (user == null)
            {
                // Graceful fallback if database was dropped but browser still has old auth cookie
                return LocalRedirect("/Identity/Account/Login?ReturnUrl=/");
            }

            var isAdmin = await _userManager.IsInRoleAsync(user, RoleNames.Admin);

            var ticketsQuery = _context.Tickets.AsNoTracking().AsQueryable();

            if (!isAdmin)
            {
                ticketsQuery = ticketsQuery.Where(t => t.DepartmentId == user!.DepartmentId || t.CreatedByUserId == userId);
            }

            var now = DateTime.UtcNow;
            var highPriorityIds = await _context.TicketPriorities
                .AsNoTracking()
                .Where(p => p.Level >= 3)
                .Select(p => p.Id)
                .ToListAsync();

            var totalTickets = await ticketsQuery.CountAsync();
            var openTickets = await ticketsQuery.CountAsync(t => openStatusIds.Contains(t.StatusId));
            var resolvedTickets = resolvedStatusId == 0 ? 0 : await ticketsQuery.CountAsync(t => t.StatusId == resolvedStatusId);
            var closedTickets = closedStatusId == 0 ? 0 : await ticketsQuery.CountAsync(t => t.StatusId == closedStatusId);
            var highPriorityTickets = await ticketsQuery.CountAsync(t => highPriorityIds.Contains(t.PriorityId));
            var ticketsCreatedToday = await ticketsQuery.CountAsync(t => t.CreatedAt >= now.Date && t.CreatedAt < now.Date.AddDays(1));
            var ticketsCreatedThisWeek = await ticketsQuery.CountAsync(t => t.CreatedAt >= now.AddDays(-7));
            var slaBreachedTickets = await ticketsQuery.CountAsync(t => t.IsSlaBreached);
            var pendingTickets = await ticketsQuery.CountAsync(t => t.PendingReason != null || t.Status!.Name == TicketStatusNames.Pending);
            var escalatedTickets = await ticketsQuery.CountAsync(t => t.EscalatedAt.HasValue);

            var recentTickets = await ticketsQuery
                .Include(t => t.Category)
                .Include(t => t.Priority)
                .Include(t => t.Source)
                .Include(t => t.Status)
                .Include(t => t.Department)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .ToListAsync();

            var statusBreakdown = await ticketsQuery
                .GroupBy(t => t.Status != null ? t.Status.Name : "Unknown")
                .Select(g => new DashboardMetricItem { Label = g.Key, Value = g.Count() })
                .OrderByDescending(g => g.Value)
                .ToListAsync();

            var sourceBreakdown = await ticketsQuery
                .GroupBy(t => t.Source != null ? t.Source.Name : TicketSourceNames.DefaultFallback)
                .Select(g => new DashboardMetricItem { Label = g.Key, Value = g.Count() })
                .OrderByDescending(g => g.Value)
                .ToListAsync();

            var weeklyFloor = now.Date.AddDays(-6);
            var weeklyVolumeRaw = await ticketsQuery
                .Where(t => t.CreatedAt >= weeklyFloor)
                .GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync();

            var weeklyVolume = Enumerable.Range(0, 7)
                .Select(offset => weeklyFloor.AddDays(offset))
                .Select(day => new DashboardMetricItem
                {
                    Label = day.ToString("ddd"),
                    Value = weeklyVolumeRaw.FirstOrDefault(x => x.Key == day)?.Count ?? 0
                })
                .ToList();

            var productAreas = await ticketsQuery
                .GroupBy(t => t.ProductArea ?? "Unassigned")
                .Select(g => new DashboardMetricItem { Label = g.Key, Value = g.Count() })
                .OrderByDescending(g => g.Value)
                .Take(5)
                .ToListAsync();

            CopilotAssessmentRunSummaryDto? assessmentSummary = null;

            var model = new DashboardViewModel
            {
                TotalTickets = totalTickets,
                OpenTickets = openTickets,
                ResolvedTickets = resolvedTickets,
                ClosedTickets = closedTickets,
                HighPriorityTickets = highPriorityTickets,
                TicketsCreatedToday = ticketsCreatedToday,
                TicketsCreatedThisWeek = ticketsCreatedThisWeek,
                SlaBreachedTickets = slaBreachedTickets,
                PendingTickets = pendingTickets,
                EscalatedTickets = escalatedTickets,
                ResolutionRatePercent = totalTickets == 0 ? 0 : Math.Round(((double)(resolvedTickets + closedTickets) / totalTickets) * 100, 1),
                SlaRiskPercent = totalTickets == 0 ? 0 : Math.Round(((double)slaBreachedTickets / totalTickets) * 100, 1),
                LatestCopilotAssessment = assessmentSummary,
                RecentTickets = recentTickets,
                StatusBreakdown = statusBreakdown,
                SourceBreakdown = sourceBreakdown,
                WeeklyVolume = weeklyVolume,
                ProductAreas = productAreas
            };

            return View(model);
        }
    }
}
