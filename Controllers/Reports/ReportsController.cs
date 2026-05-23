using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Models.DTOs;
using AutoMapper;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI;
using ServiceOpsAI.Services.AI.Copilot.Assessment;
using ServiceOpsAI.Services.AI.Copilot.Tools;

namespace ServiceOpsAI.Controllers.Reports
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IMapper _mapper;
        private readonly CopilotToolRegistry _toolRegistry;
        private readonly KnowledgeBaseRagService _knowledgeBaseRagService;
        private readonly CopilotAssessmentHandler _assessmentService;

        public ReportsController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IMapper mapper,
            CopilotToolRegistry toolRegistry,
            KnowledgeBaseRagService knowledgeBaseRagService,
            CopilotAssessmentHandler assessmentService)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
            _toolRegistry = toolRegistry;
            _knowledgeBaseRagService = knowledgeBaseRagService;
            _assessmentService = assessmentService;
        }

        public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate)
        {
            var userId = _userManager.GetUserId(User);
            var user = await _userManager.FindByIdAsync(userId!);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            ViewBag.IsAdmin = isAdmin;

            var normalizedStartDate = startDate?.Date;
            var normalizedEndDate = endDate?.Date;

            if (normalizedStartDate.HasValue && normalizedEndDate.HasValue && normalizedStartDate > normalizedEndDate)
            {
                (normalizedStartDate, normalizedEndDate) = (normalizedEndDate, normalizedStartDate);
            }

            var endDateExclusive = normalizedEndDate?.AddDays(1);

            var query = _context.Tickets.AsNoTracking().AsQueryable();

            if (!isAdmin)
            {
                query = query.Where(t => t.DepartmentId == user!.DepartmentId || t.CreatedByUserId == userId);
            }

            if (normalizedStartDate.HasValue) query = query.Where(t => t.CreatedAt >= normalizedStartDate.Value);
            if (endDateExclusive.HasValue) query = query.Where(t => t.CreatedAt < endDateExclusive.Value);

            ViewBag.StartDate = normalizedStartDate?.ToString("yyyy-MM-dd");
            ViewBag.EndDate = normalizedEndDate?.ToString("yyyy-MM-dd");
            ViewBag.TotalTickets = await query.CountAsync();
            ViewBag.SlaBreachedTickets = await query.CountAsync(t => t.IsSlaBreached);
            ViewBag.EscalatedTickets = await query.CountAsync(t => t.EscalatedAt.HasValue);
            ViewBag.HighImpactTickets = await query.CountAsync(t => t.AffectedUsersCount.HasValue && t.AffectedUsersCount >= 20);

            ViewBag.ByStatus = await query
                .GroupBy(t => t.Status != null ? t.Status.Name : "Unknown")
                .Select(g => new ReportMetricDto { Label = g.Key, Count = g.Count() })
                .ToListAsync();

            ViewBag.ByPriority = await query
                .GroupBy(t => t.Priority != null ? t.Priority.Name : "Unknown")
                .Select(g => new ReportMetricDto { Label = g.Key, Count = g.Count() })
                .ToListAsync();

            ViewBag.ByCategory = await query
                .GroupBy(t => t.Category != null ? t.Category.Name : "Unknown")
                .Select(g => new ReportMetricDto { Label = g.Key, Count = g.Count() })
                .ToListAsync();

            ViewBag.ByEntity = await query
                .GroupBy(t => t.Department != null ? t.Department.Name : "Unknown")
                .Select(g => new ReportMetricDto { Label = g.Key, Count = g.Count() })
                .ToListAsync();

            ViewBag.BySource = await query
                .GroupBy(t => t.Source != null ? t.Source.Name : TicketSourceNames.DefaultFallback)
                .Select(g => new ReportMetricDto { Label = g.Key, Count = g.Count() })
                .ToListAsync();

            var trendFloor = DateTime.UtcNow.AddMonths(-6);
            var monthlyTrend = await query
                .Where(t => t.CreatedAt >= trendFloor)
                .GroupBy(t => new { t.CreatedAt.Year, t.CreatedAt.Month })
                .OrderBy(g => g.Key.Year)
                .ThenBy(g => g.Key.Month)
                .Select(g => new ReportMetricDto { Label = $"{new DateTime(g.Key.Year, g.Key.Month, 1):MMM yy}", Count = g.Count() })
                .ToListAsync();

            ViewBag.MonthlyData = monthlyTrend;

            return View();
        }

        [Authorize(Roles = RoleNames.Admin)]
        [HttpGet]
        public async Task<IActionResult> Copilot(string? prompt = null, DateTime? startDate = null, DateTime? endDate = null)
        {
            var recentTickets = await _context.Tickets
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAt)
                .Take(20)
                .Select(t => new CopilotEvaluationTicketItem
                {
                    TicketId = t.Id,
                    TicketNumber = t.TicketNumber,
                    Title = t.Title,
                    Status = t.Status != null ? t.Status.Name : "",
                    ProductArea = t.ProductArea ?? "",
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            var recentTraces = await _context.CopilotTraceHistories
                .OrderByDescending(t => t.CreatedAt)
                .Take(10)
                .ToListAsync();

            var model = new CopilotChatViewModel
            {
                RecentTickets = recentTickets,
                AvailableTools = (await _toolRegistry.GetAllToolsAsync()).Where(t => t.IsEnabled).ToList(),
                StandardPromptGroups = await _assessmentService.GetCopilotPromptGroupsAsync("reports"),
                RecentTraces = recentTraces,
                KnowledgeDocumentCount = _knowledgeBaseRagService.GetDocumentCount()
            };

            ViewData["CopilotPageTitle"] = "Reports Copilot";
            ViewData["CopilotHeaderTitle"] = "Reports Copilot";
            ViewData["CopilotHeaderBadge"] = "Reporting";
            ViewData["CopilotHeaderHint"] = "Ask for trends, top entities, SLA breaches, source mix, and multi-query comparisons.";
            ViewData["CopilotExitController"] = "Reports";
            ViewData["CopilotExitAction"] = "Index";
            ViewData["CopilotSurface"] = "reports";
            ViewData["CopilotInitialPrompt"] = prompt ?? string.Empty;
            ViewData["CopilotReportStartDate"] = startDate?.Date.ToString("yyyy-MM-dd") ?? string.Empty;
            ViewData["CopilotReportEndDate"] = endDate?.Date.ToString("yyyy-MM-dd") ?? string.Empty;
            return View("~/Views/AiAnalysis/Copilot.cshtml", model);
        }
    }
}
