using AutoMapper;
using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Models.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using AISupportAnalysisPlatform.Services.Notifications;
using AISupportAnalysisPlatform.Services.AI;

using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Models.DTOs;
using AutoMapper.QueryableExtensions;

namespace AISupportAnalysisPlatform.Controllers.Tickets
{
    [Authorize]
    public class TicketsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notificationService;
        private readonly IAiAnalysisService _aiAnalysisService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly IMapper _mapper;

        public TicketsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, INotificationService notificationService, IAiAnalysisService aiAnalysisService, IServiceScopeFactory scopeFactory, ISemanticSearchService semanticSearchService, IMapper mapper)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
            _aiAnalysisService = aiAnalysisService;
            _scopeFactory = scopeFactory;
            _semanticSearchService = semanticSearchService;
            _mapper = mapper;
        }

        private async Task PopulateTicketFormLookupsAsync(
            ApplicationUser user,
            bool isAdmin,
            bool includeEntityScope,
            Ticket? ticket = null)
        {
            ViewData["CategoryId"] = new SelectList(_context.TicketCategories.Where(c => c.IsActive), "Id", "Name", ticket?.CategoryId);
            ViewData["PriorityId"] = new SelectList(_context.TicketPriorities.Where(p => p.IsActive), "Id", "Name", ticket?.PriorityId);
            ViewData["SourceId"] = new SelectList(_context.TicketSources.Where(s => s.IsActive), "Id", "Name", ticket?.SourceId);
            ViewData["StatusId"] = new SelectList(_context.TicketStatuses.Where(s => s.IsActive), "Id", "Name", ticket?.StatusId);

            if (includeEntityScope)
            {
                if (isAdmin)
                {
                    ViewData["EntityId"] = new SelectList(_context.Entities.Where(e => e.IsActive), "Id", "Name", ticket?.EntityId);
                }
                else
                {
                    ViewData["EntityId"] = new SelectList(
                        _context.Entities.Where(e => e.Id == user.EntityId && e.IsActive),
                        "Id",
                        "Name",
                        ticket?.EntityId ?? user.EntityId);
                }
            }

            var agents = await _userManager.GetUsersInRoleAsync(RoleNames.SupportAgent);
            var admins = await _userManager.GetUsersInRoleAsync(RoleNames.Admin);
            var allStaff = agents.Concat(admins).DistinctBy(u => u.Id).OrderBy(u => u.Email).ToList();

            ViewData["AssignedToUserId"] = new SelectList(allStaff, "Id", "Email", ticket?.AssignedToUserId);
            ViewData["EscalatedToUserId"] = new SelectList(allStaff, "Id", "Email", ticket?.EscalatedToUserId);
            ViewData["ResolutionApprovedByUserId"] = new SelectList(allStaff, "Id", "Email", ticket?.ResolutionApprovedByUserId);

            var availableParentsQuery = _context.Tickets.Where(t => !t.IsDeleted);
            if (ticket != null)
            {
                availableParentsQuery = availableParentsQuery.Where(t => t.Id != ticket.Id);
            }
            if (includeEntityScope && !isAdmin)
            {
                availableParentsQuery = availableParentsQuery.Where(t => t.EntityId == user.EntityId);
            }
            var parentsList = await availableParentsQuery
                .ProjectTo<LookupDisplayDto>(_mapper.ConfigurationProvider)
                .ToListAsync();
            ViewData["ParentTicketId"] = new SelectList(parentsList, "Id", "Display", ticket?.ParentTicketId);
        }

        // GET: Tickets
        public async Task<IActionResult> Index([FromQuery] GridRequestModel request)
        {
            request.Normalize();

            var user = await _userManager.GetUserAsync(User);
            var userId = user?.Id;
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);

            var tickets = _context.Tickets
                .AsNoTracking()
                .Include(t => t.AssignedToUser)
                .Include(t => t.Category)
                .Include(t => t.CreatedByUser)
                .Include(t => t.Entity)
                .Include(t => t.Priority)
                .Include(t => t.Source)
                .Include(t => t.Status)
                .Include(t => t.Attachments)
                .Where(t => !t.IsDeleted)
                .AsQueryable();

            if (!isAdmin)
            {
                tickets = tickets.Where(t => t.EntityId == user!.EntityId || t.CreatedByUserId == userId);
            }

            switch (request.Filter?.ToLower())
            {
                case "mytickets":
                    tickets = tickets.Where(t => t.CreatedByUserId == userId);
                    break;
                case "assignedtome":
                    tickets = tickets.Where(t => t.AssignedToUserId == userId);
                    break;
                case "openhighpriority":
                    tickets = tickets.Where(t => !t.Status!.IsClosedState && t.Priority!.Level >= 3);
                    break;
                case "duetoday":
                    var today = DateTime.UtcNow.Date;
                    tickets = tickets.Where(t => t.ResolutionDueAt.HasValue && t.ResolutionDueAt.Value.Date == today);
                    break;
                case "recentlyupdated":
                    tickets = tickets.Where(t => t.UpdatedAt.HasValue && t.UpdatedAt > DateTime.UtcNow.AddHours(-24));
                    break;
            }

            if (!string.IsNullOrEmpty(request.SearchString))
            {
                tickets = tickets.Where(t =>
                    t.TicketNumber.Contains(request.SearchString) ||
                    t.Title.Contains(request.SearchString) ||
                    (t.ProductArea != null && t.ProductArea.Contains(request.SearchString)) ||
                    (t.ExternalReferenceId != null && t.ExternalReferenceId.Contains(request.SearchString)) ||
                    (t.ExternalSystemName != null && t.ExternalSystemName.Contains(request.SearchString)) ||
                    (t.BrowserName != null && t.BrowserName.Contains(request.SearchString)));
            }
            if (request.StatusId.HasValue) tickets = tickets.Where(t => t.StatusId == request.StatusId);
            if (request.PriorityId.HasValue) tickets = tickets.Where(t => t.PriorityId == request.PriorityId);
            if (request.CategoryId.HasValue) tickets = tickets.Where(t => t.CategoryId == request.CategoryId);
            if (request.EntityId.HasValue) tickets = tickets.Where(t => t.EntityId == request.EntityId);

            switch (request.SortOrder)
            {
                case "id_desc": tickets = tickets.OrderByDescending(t => t.TicketNumber); break;
                case "Id": tickets = tickets.OrderBy(t => t.TicketNumber); break;
                case "date_desc": tickets = tickets.OrderByDescending(t => t.CreatedAt); break;
                case "Title": tickets = tickets.OrderBy(t => t.Title); break;
                case "title_desc": tickets = tickets.OrderByDescending(t => t.Title); break;
                case "Entity": tickets = tickets.OrderBy(t => t.Entity!.Name); break;
                case "entity_desc": tickets = tickets.OrderByDescending(t => t.Entity!.Name); break;
                case "Status": tickets = tickets.OrderBy(t => t.Status!.Name); break;
                case "status_desc": tickets = tickets.OrderByDescending(t => t.Status!.Name); break;
                default: tickets = tickets.OrderByDescending(t => t.CreatedAt); break;
            }

            var totalCount = await tickets.CountAsync();
            var effectivePageSize = request.GetEffectivePageSize(totalCount);

            var items = await tickets.Skip((request.PageNumber - 1) * effectivePageSize)
                                     .Take(effectivePageSize)
                                     .ToListAsync();

            var pagedResult = new PagedResult<Ticket>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };

            await PopulateTicketFormLookupsAsync(user!, isAdmin, true);

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_QueueGrid", pagedResult);
            }

            if (isAdmin)
            {
                ViewData["EntityFilterId"] = new SelectList(_context.Entities, "Id", "Name", request.EntityId);
            }
            ViewBag.IsAdmin = isAdmin;

            return View(pagedResult);
        }

        // GET: Tickets/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var ticket = await _context.Tickets
                .Include(t => t.AssignedToUser)
                .Include(t => t.EscalatedToUser)
                .Include(t => t.Category)
                .Include(t => t.CreatedByUser)
                .Include(t => t.Entity)
                .Include(t => t.Priority)
                .Include(t => t.ResolutionApprovedByUser)
                .Include(t => t.ResolvedByUser)
                .Include(t => t.Source)
                .Include(t => t.Status)
                .Include(t => t.Comments).ThenInclude(c => c.CreatedByUser)
                .Include(t => t.Comments).ThenInclude(c => c.Attachments)
                .Include(t => t.Attachments).ThenInclude(a => a.UploadedByUser)
                .Include(t => t.HistoryRecords).ThenInclude(h => h.User)
                .Include(t => t.ParentTicket)
                .Include(t => t.ChildTickets)
                .AsSplitQuery()
                .FirstOrDefaultAsync(m => m.Id == id);
                
            if (ticket == null) return NotFound();

            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            var isAgent = await _userManager.IsInRoleAsync(user!, RoleNames.SupportAgent);

            ViewBag.IsAdmin = isAdmin;
            ViewBag.IsAgent = isAgent;
            await PopulateTicketFormLookupsAsync(user!, isAdmin, includeEntityScope: true, ticket);

            // Load AI investigation for admin
            if (isAdmin)
            {
                ViewBag.AiAnalysis = await _aiAnalysisService.GetLatestTicketAnalysisAsync(ticket.Id);
                var defaultSemanticStatuses = await _context.TicketStatuses
                    .Where(s => s.Name == TicketStatusNames.Resolved || s.Name == TicketStatusNames.Closed)
                    .Select(s => s.Id)
                    .ToListAsync();
                ViewBag.SemanticStatusOptions = await _context.TicketStatuses
                    .OrderBy(s => s.Name)
                    .Select(s => new SelectListItem
                    {
                        Value = s.Id.ToString(),
                        Text = s.Name,
                        Selected = defaultSemanticStatuses.Contains(s.Id)
                    })
                    .ToListAsync();
                
                // DIAGNOSTIC telemetry: check how many resolved tickets have embeddings
                ViewBag.TicketCandidateCount = await _context.TicketSemanticEmbeddings
                    .Where(e => e.Ticket!.Status!.Name == TicketStatusNames.Resolved || e.Ticket!.Status!.Name == TicketStatusNames.Closed)
                    .CountAsync();
            }

            return View(ticket);
        }

        // GET: Tickets/Create
        public async Task<IActionResult> Create()
        {
            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            await PopulateTicketFormLookupsAsync(user!, isAdmin, includeEntityScope: true);
            ViewBag.IsAdmin = isAdmin;
            ViewBag.IsAgent = await _userManager.IsInRoleAsync(user!, RoleNames.SupportAgent);
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Title,Description,CategoryId,PriorityId,SourceId,EntityId,StatusId,AssignedToUserId,DueDate,ProductArea,EnvironmentName,BrowserName,OperatingSystem,ExternalReferenceId,ExternalSystemName,ImpactScope,AffectedUsersCount,ParentTicketId")] Ticket ticket, List<IFormFile> files)
        {
            ModelState.Remove("TicketNumber");
            ModelState.Remove("CreatedByUserId");

            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);

            if (ModelState.IsValid)
            {
                ticket.CreatedByUserId = user!.Id;
                ticket.CreatedAt = DateTime.UtcNow;
                ticket.TicketNumber = $"TCK-{DateTime.UtcNow.Year}-{new Random().Next(10000, 99999)}";
                
                // Force Entity if not admin
                if (!isAdmin)
                {
                    ticket.EntityId = user.EntityId;
                }
                else if (ticket.EntityId == null || ticket.EntityId == 0)
                {
                    ticket.EntityId = user.EntityId;
                }

                // Force "New" Status for all new tickets
                var newStatus = await _context.TicketStatuses.FirstOrDefaultAsync(s => s.Name == TicketStatusNames.New);
                if (newStatus != null)
                {
                    ticket.StatusId = newStatus.Id;
                }

                ticket.FirstResponseDueAt = ticket.CreatedAt.AddHours(4);
                ticket.ResolutionDueAt = ticket.CreatedAt.AddDays(2);

                _context.Add(ticket);
                await _context.SaveChangesAsync();

                var admins = await _userManager.GetUsersInRoleAsync(RoleNames.Admin);
                foreach (var admin in admins)
                {
                    await _notificationService.CreateNotificationAsync(admin.Id, "Global System Alert", $"Critical: New Ticket {ticket.TicketNumber} created.", $"/Tickets/Details/{ticket.Id}");
                }

                if (!string.IsNullOrEmpty(ticket.AssignedToUserId))
                {
                    await _notificationService.CreateNotificationAsync(ticket.AssignedToUserId, "New Ticket Assigned", $"You have been assigned to Ticket {ticket.TicketNumber}", $"/Tickets/Details/{ticket.Id}");
                }

                if (files != null && files.Count > 0)
                {
                    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                    foreach (var file in files)
                    {
                        if (file.Length > 0)
                        {
                            var uniqueFileName = Guid.NewGuid().ToString() + "_" + file.FileName;
                            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                            using (var stream = new FileStream(filePath, FileMode.Create))
                            {
                                await file.CopyToAsync(stream);
                            }

                            var attachment = new TicketAttachment
                            {
                                TicketId = ticket.Id,
                                FileName = file.FileName,
                                FilePath = "/uploads/" + uniqueFileName,
                                ContentType = file.ContentType,
                                FileSize = file.Length,
                                UploadedByUserId = ticket.CreatedByUserId
                            };
                            _context.TicketAttachments.Add(attachment);
                        }
                    }
                }
                
                _context.TicketHistories.Add(new TicketHistory {
                    TicketId = ticket.Id,
                    Action = "Created - SLA Tracked",
                    UserId = ticket.CreatedByUserId,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                
                return RedirectToAction(nameof(Index));
            }
            return View(ticket);
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var ticket = await _context.Tickets
                .Include(t => t.Status)
                .Include(t => t.EscalatedToUser)
                .Include(t => t.ResolutionApprovedByUser)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (ticket == null || ticket.IsDeleted) return NotFound();

            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            var isAgent = await _userManager.IsInRoleAsync(user!, RoleNames.SupportAgent);

            // REAL-APP LOGIC: Field Operative restriction
            if (!isAdmin && !isAgent)
            {
                if (ticket.CreatedByUserId != user!.Id) return Forbid();
                if (ticket.Status?.Name != TicketStatusNames.New)
                {
                    TempData["Error"] = "Operational Lock: Case is currently being processed by the technical team and is immutable to the originator. Please use the discussion thread for updates.";
                    return RedirectToAction(nameof(Details), new { id });
                }
            }

            await PopulateTicketFormLookupsAsync(user!, isAdmin, includeEntityScope: true, ticket);
            ViewBag.IsAdmin = isAdmin;
            ViewBag.IsAgent = isAgent;
            return View(ticket);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("Id,Title,Description,CategoryId,PriorityId,SourceId,EntityId,StatusId,AssignedToUserId,DueDate,ResolutionSummary,PendingReason,ProductArea,EnvironmentName,BrowserName,OperatingSystem,ExternalReferenceId,ExternalSystemName,ImpactScope,AffectedUsersCount,TechnicalAssessment,EscalationLevel,EscalatedToUserId,RootCause,VerificationNotes,ResolutionApprovedByUserId,ParentTicketId")] Ticket ticket, List<IFormFile> files)
        {
            if (id != ticket.Id) return NotFound();

            var existingTicket = await _context.Tickets.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == id);
            if (existingTicket == null) return NotFound();

            var loggedInUserId = _userManager.GetUserId(User);
            var loggedInUser = await _userManager.FindByIdAsync(loggedInUserId!);
            var isUserAdmin = await _userManager.IsInRoleAsync(loggedInUser!, RoleNames.Admin);

                if (!isUserAdmin && existingTicket.EntityId != loggedInUser!.EntityId && existingTicket.CreatedByUserId != loggedInUserId)
                {
                    return Forbid();
                }

            ModelState.Remove("TicketNumber");
            ModelState.Remove("CreatedByUserId");

            if (ModelState.IsValid)
            {
                var newStatus = await _context.TicketStatuses.FindAsync(ticket.StatusId);
                var oldStatus = await _context.TicketStatuses.FindAsync(existingTicket.StatusId);
                var user = await _userManager.GetUserAsync(User);
                var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
                var isAgent = await _userManager.IsInRoleAsync(user!, RoleNames.SupportAgent);

                // REAL-APP LOGIC: Who can change phase?
                if (ticket.StatusId != existingTicket.StatusId && !isAdmin && !isAgent)
                {
                    ModelState.AddModelError("StatusId", "Authority Protocol Violation: Case lifecycle phases can only be transitioned by a Technical Lead or Admin.");
                }

                // REAL-APP LOGIC: Operative editing locked case
                if (!isAdmin && !isAgent && existingTicket.Status?.Name != TicketStatusNames.New)
                {
                    ModelState.AddModelError("Title", "Operational Lock: Case details are immutable once processing has commenced.");
                }

                bool assignedChanged = existingTicket.AssignedToUserId != ticket.AssignedToUserId;
                bool statusChanged = existingTicket.StatusId != ticket.StatusId;

                // REAL-APP LOGIC: Authority bypass for Admins
                if (!isAdmin)
                {
                    if (newStatus!.Name == TicketStatusNames.Closed && (oldStatus?.Name != TicketStatusNames.Resolved && oldStatus?.Name != TicketStatusNames.Rejected))
                    {
                        ModelState.AddModelError("StatusId", "Protocol Violation: Case must be Resolved or Rejected before final deactivation.");
                    }
                    if (newStatus.Name == TicketStatusNames.Resolved && string.IsNullOrWhiteSpace(ticket.ResolutionSummary))
                    {
                        ModelState.AddModelError("ResolutionSummary", "Professional resolution summary is mandatory for technical closure.");
                    }
                }
                else
                {
                    // Admin still needs a summary if they are SETTING it to Resolved right now, 
                    // but we allow them to save an existing Resolved ticket that lacks one (legacy/seed fix).
                    if (statusChanged && newStatus!.Name == TicketStatusNames.Resolved && string.IsNullOrWhiteSpace(ticket.ResolutionSummary))
                    {
                        ModelState.AddModelError("ResolutionSummary", "Admin: Please provide a summary when resolving this case.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(ticket.EscalationLevel) && string.IsNullOrWhiteSpace(ticket.EscalatedToUserId))
                {
                    ModelState.AddModelError("EscalatedToUserId", "Select the engineer or lead who owns the escalation.");
                }

                if (!string.IsNullOrWhiteSpace(ticket.EscalatedToUserId) && string.IsNullOrWhiteSpace(ticket.EscalationLevel))
                {
                    ModelState.AddModelError("EscalationLevel", "Provide the escalation level when routing this ticket.");
                }

                if (statusChanged && newStatus!.Name == TicketStatusNames.Resolved && string.IsNullOrWhiteSpace(ticket.VerificationNotes))
                {
                    ModelState.AddModelError("VerificationNotes", "Verification notes are required before resolving the ticket.");
                }

                if (statusChanged && newStatus?.Name == TicketStatusNames.Closed && !isAdmin && string.IsNullOrWhiteSpace(ticket.ResolutionApprovedByUserId))
                {
                    ModelState.AddModelError("ResolutionApprovedByUserId", "Resolution approval must be assigned before closing the ticket.");
                }

                if (!ModelState.IsValid)
                {
                    await PopulateTicketFormLookupsAsync(user!, isAdmin, includeEntityScope: true, ticket);
                    ViewBag.IsAdmin = isAdmin;
                    ViewBag.IsAgent = isAgent;
                    return View(ticket);
                }

                if (statusChanged && oldStatus?.Name == TicketStatusNames.New)
                {
                    existingTicket.FirstRespondedAt = DateTime.UtcNow;
                    if (existingTicket.FirstRespondedAt > existingTicket.FirstResponseDueAt)
                    {
                        existingTicket.IsSlaBreached = true;
                    }
                }

                existingTicket.Title = ticket.Title;
                existingTicket.Description = ticket.Description;
                existingTicket.CategoryId = ticket.CategoryId;
                existingTicket.PriorityId = ticket.PriorityId;
                existingTicket.SourceId = ticket.SourceId;
                existingTicket.EntityId = ticket.EntityId;
                existingTicket.StatusId = ticket.StatusId;
                existingTicket.AssignedToUserId = ticket.AssignedToUserId;
                existingTicket.DueDate = ticket.DueDate;
                existingTicket.ProductArea = ticket.ProductArea;
                existingTicket.EnvironmentName = ticket.EnvironmentName;
                existingTicket.BrowserName = ticket.BrowserName;
                existingTicket.OperatingSystem = ticket.OperatingSystem;
                existingTicket.ExternalReferenceId = ticket.ExternalReferenceId;
                existingTicket.ExternalSystemName = ticket.ExternalSystemName;
                existingTicket.ImpactScope = ticket.ImpactScope;
                existingTicket.AffectedUsersCount = ticket.AffectedUsersCount;
                existingTicket.ParentTicketId = ticket.ParentTicketId;
                existingTicket.TechnicalAssessment = ticket.TechnicalAssessment;
                existingTicket.EscalationLevel = ticket.EscalationLevel;
                existingTicket.EscalatedToUserId = ticket.EscalatedToUserId;
                existingTicket.ResolutionSummary = ticket.ResolutionSummary;
                existingTicket.PendingReason = ticket.PendingReason;
                existingTicket.RootCause = ticket.RootCause;
                existingTicket.VerificationNotes = ticket.VerificationNotes;
                existingTicket.ResolutionApprovedByUserId = ticket.ResolutionApprovedByUserId;
                existingTicket.UpdatedAt = DateTime.UtcNow;

                var uId = _userManager.GetUserId(User)!;

                var escalationActive = !string.IsNullOrWhiteSpace(existingTicket.EscalationLevel) && !string.IsNullOrWhiteSpace(existingTicket.EscalatedToUserId);
                if (escalationActive)
                {
                    existingTicket.EscalatedAt ??= DateTime.UtcNow;
                }
                else
                {
                    existingTicket.EscalatedAt = null;
                }

                if (statusChanged)
                {
                    if (newStatus?.Name == TicketStatusNames.Resolved)
                    {
                        existingTicket.ResolvedAt = DateTime.UtcNow;
                        existingTicket.ResolvedByUserId = uId;
                    }

                    if (newStatus?.Name == TicketStatusNames.Closed)
                    {
                        existingTicket.ClosedAt = DateTime.UtcNow;
                        if (string.IsNullOrWhiteSpace(existingTicket.ResolutionApprovedByUserId))
                        {
                            existingTicket.ResolutionApprovedByUserId = uId;
                        }
                    }

                    _context.TicketHistories.Add(new TicketHistory { TicketId = id, Action = $"Status updated: {oldStatus?.Name} -> {newStatus?.Name}", UserId = uId });
                    await _notificationService.CreateNotificationAsync(existingTicket.CreatedByUserId, "Case Status Update", $"Your Case {existingTicket.TicketNumber} is now {newStatus?.Name}.", $"/Tickets/Details/{id}");
                }

                if (!string.IsNullOrWhiteSpace(existingTicket.ResolutionApprovedByUserId))
                {
                    existingTicket.ResolutionApprovedAt ??= DateTime.UtcNow;
                }
                else
                {
                    existingTicket.ResolutionApprovedAt = null;
                }

                if (assignedChanged)
                {
                    _context.TicketHistories.Add(new TicketHistory { TicketId = id, Action = "Ownership Transfer", UserId = uId });
                    if (!string.IsNullOrEmpty(existingTicket.AssignedToUserId))
                    {
                        await _notificationService.CreateNotificationAsync(existingTicket.AssignedToUserId, "New Assignment", $"You are now the lead specialist for Case {existingTicket.TicketNumber}.", $"/Tickets/Details/{id}");
                    }
                }

                if (escalationActive)
                {
                    _context.TicketHistories.Add(new TicketHistory
                    {
                        TicketId = id,
                        Action = $"Escalation routed at {existingTicket.EscalationLevel} level",
                        UserId = uId
                    });

                    if (!string.IsNullOrWhiteSpace(existingTicket.EscalatedToUserId))
                    {
                        await _notificationService.CreateNotificationAsync(existingTicket.EscalatedToUserId, "Ticket Escalation", $"Case {existingTicket.TicketNumber} has been escalated to you.", $"/Tickets/Details/{id}");
                    }
                }

                if (files != null && files.Count > 0)
                {
                    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                    foreach (var file in files)
                    {
                        if (file.Length > 0)
                        {
                            var uniqueFileName = Guid.NewGuid().ToString() + "_" + file.FileName;
                            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                            using (var stream = new FileStream(filePath, FileMode.Create))
                            {
                                await file.CopyToAsync(stream);
                            }

                            var attachment = new TicketAttachment
                            {
                                TicketId = id,
                                FileName = file.FileName,
                                FilePath = "/uploads/" + uniqueFileName,
                                ContentType = file.ContentType,
                                FileSize = file.Length,
                                UploadedByUserId = uId
                            };
                            _context.TicketAttachments.Add(attachment);
                        }
                    }
                }

                _context.Update(existingTicket);
                await _context.SaveChangesAsync();

                return RedirectToAction(nameof(Index));
            }
            return View(ticket);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var ticket = await _context.Tickets.FindAsync(id);
            if (ticket != null)
            {
                ticket.IsDeleted = true;
                _context.Update(ticket);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuickOverride(int id, int? statusId, int? priorityId, int? entityId)
        {
            var ticket = await _context.Tickets.Include(t => t.Status).Include(t => t.Priority).FirstOrDefaultAsync(t => t.Id == id);
            if (ticket == null) return NotFound();

            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            if (!isAdmin) return Forbid();

            var historyDetails = new List<string>();

            if (statusId.HasValue && ticket.StatusId != statusId.Value)
            {
                var newStatus = await _context.TicketStatuses.FindAsync(statusId.Value);
                if (newStatus != null)
                {
                    historyDetails.Add($"Phase: {ticket.Status?.Name} -> {newStatus.Name}");
                    ticket.StatusId = statusId.Value;
                }
            }

            if (priorityId.HasValue && ticket.PriorityId != priorityId.Value)
            {
                var newPriority = await _context.TicketPriorities.FindAsync(priorityId.Value);
                if (newPriority != null)
                {
                    historyDetails.Add($"Priority: {ticket.Priority?.Name} -> {newPriority.Name}");
                    ticket.PriorityId = priorityId.Value;
                }
            }

            if (entityId.HasValue && ticket.EntityId != entityId.Value)
            {
                var newEntity = await _context.Entities.FindAsync(entityId.Value);
                if (newEntity != null)
                {
                    historyDetails.Add($"Entity: {ticket.Entity?.Name} -> {newEntity.Name}");
                    ticket.EntityId = entityId.Value;
                }
            }

            if (historyDetails.Any())
            {
                ticket.UpdatedAt = DateTime.UtcNow;
                _context.TicketHistories.Add(new TicketHistory {
                    TicketId = ticket.Id,
                    Action = $"Admin Tactical Override: {string.Join(", ", historyDetails)}",
                    UserId = user!.Id,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                TempData["Success"] = "Tactical Override Synchronized Successfully.";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, int statusId)
        {
            var ticket = await _context.Tickets.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == id);
            if (ticket == null) return NotFound();

            var user = await _userManager.GetUserAsync(User);
            var isAdmin = await _userManager.IsInRoleAsync(user!, RoleNames.Admin);
            var isAgent = await _userManager.IsInRoleAsync(user!, "SupportAgent");

            if (!isAdmin && !isAgent) return Forbid();

            var oldStatusName = ticket.Status?.Name;
            var newStatus = await _context.TicketStatuses.FindAsync(statusId);
            if (newStatus == null) return NotFound();

            // Transition Constraints
            if (newStatus.Name == TicketStatusNames.Closed && (oldStatusName != TicketStatusNames.Resolved && oldStatusName != TicketStatusNames.Rejected))
            {
                TempData["Error"] = "Authority Override: Case must transit through 'Resolved' for validation before final closure.";
                return RedirectToAction(nameof(Details), new { id });
            }
            if (newStatus.Name == "Resolved" && string.IsNullOrWhiteSpace(ticket.ResolutionSummary))
            {
                TempData["Error"] = "Authorization Required: A technical resolution summary must be documented before classification as 'Resolved'.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Ingestion-to-Response SLA
            if (oldStatusName == TicketStatusNames.New)
            {
                ticket.FirstRespondedAt = DateTime.UtcNow;
                if (ticket.FirstRespondedAt > ticket.FirstResponseDueAt)
                {
                    ticket.IsSlaBreached = true;
                }
            }

            if (newStatus.Name == "Resolved")
            {
                ticket.ResolvedAt = DateTime.UtcNow;
                if (ticket.ResolvedAt > ticket.ResolutionDueAt)
                {
                    ticket.IsSlaBreached = true;
                }
            }

            ticket.StatusId = statusId;
            await _context.SaveChangesAsync();
            
            _context.TicketHistories.Add(new TicketHistory {
                TicketId = ticket.Id,
                Action = $"Phase Transition: {oldStatusName} -> {newStatus.Name}",
                UserId = user!.Id,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Case Phase Updated to {newStatus.Name}";
            return RedirectToAction(nameof(Details), new { id });
        }

        // ─── AI Investigation Actions (Admin Only) ──────────────────────────────

        [HttpPost]
        [IgnoreAntiforgeryToken]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IActionResult> RunSemanticSearch(int id, [FromQuery] List<int>? statusIds = null, int count = 5)
        {
            try
            {
                count = Math.Clamp(count, 1, 50);
                statusIds = statusIds?.Distinct().ToList() ?? new List<int>();
                var totalStatuses = await _context.TicketStatuses.CountAsync();
                var includeAllStatuses = statusIds.Count > 0 && statusIds.Count >= totalStatuses;

                var embeddingPoolQuery = _context.TicketSemanticEmbeddings.AsQueryable();
                if (statusIds.Count > 0 && !includeAllStatuses)
                {
                    embeddingPoolQuery = embeddingPoolQuery.Where(e => statusIds.Contains(e.Ticket!.StatusId));
                }
                else if (!includeAllStatuses)
                {
                    embeddingPoolQuery = embeddingPoolQuery.Where(e => e.Ticket!.Status!.Name == TicketStatusNames.Resolved || e.Ticket!.Status!.Name == TicketStatusNames.Closed);
                }

                var poolCount = await embeddingPoolQuery.CountAsync();
                
                if (poolCount == 0)
                {
                    var bootstrapQuery = _context.Tickets.AsQueryable();
                    if (statusIds.Count > 0 && !includeAllStatuses)
                    {
                        bootstrapQuery = bootstrapQuery.Where(t => statusIds.Contains(t.StatusId));
                    }
                    else if (!includeAllStatuses)
                    {
                        bootstrapQuery = bootstrapQuery.Where(t => t.Status!.Name == TicketStatusNames.Resolved || t.Status!.Name == TicketStatusNames.Closed);
                    }

                    var bootstrapIds = await bootstrapQuery.Select(t => t.Id).ToListAsync();
                    
                    foreach (var tid in bootstrapIds)
                    {
                        await _semanticSearchService.UpsertTicketEmbeddingAsync(tid);
                    }

                    poolCount = await embeddingPoolQuery.CountAsync();
                }

                // Now generate/update embedding for current ticket and search
                await _semanticSearchService.UpsertTicketEmbeddingAsync(id);
                var related = await _semanticSearchService.GetRelatedTicketsAsync(id, count, statusIds, includeAllStatuses);
                var scopeLabel = includeAllStatuses
                    ? "All Statuses"
                    : statusIds.Count > 0
                        ? string.Join(", ", await _context.TicketStatuses
                            .Where(s => statusIds.Contains(s.Id))
                            .OrderBy(s => s.Name)
                            .Select(s => s.Name)
                            .ToListAsync())
                        : "Resolved / Closed";
                
                return Json(ApiResponse<SemanticSearchResponseDto>.Ok(new SemanticSearchResponseDto
                {
                    PoolSize = poolCount,
                    ScopeLabel = scopeLabel,
                    Data = _mapper.Map<List<SemanticSearchDataDto>>(related)
                }));
            }
            catch (Exception ex)
            {
                return Json(ApiResponse.Fail(ex.Message));
            }
        }

        // ─── One-Time Admin Bootstrapper ──────────────────────────────
        [HttpGet("Tickets/BootstrapEmbeddings")]
        [IgnoreAntiforgeryToken]
        [AllowAnonymous] // So you can run it easily for testing
        public async Task<IActionResult> BootstrapEmbeddings()
        {
            var resolvedTicketIds = await _context.Tickets
                .Where(t => t.Status != null && (t.Status.Name == TicketStatusNames.Resolved || t.Status.Name == TicketStatusNames.Closed))
                .Select(t => t.Id)
                .ToListAsync();

            int count = 0;
            foreach(var ticketId in resolvedTicketIds)
            {
                var existing = await _context.TicketSemanticEmbeddings.FindAsync(ticketId);
                if (existing == null) 
                {
                    await _semanticSearchService.UpsertTicketEmbeddingAsync(ticketId);
                    var created = await _context.TicketSemanticEmbeddings.FindAsync(ticketId);
                    if (created != null)
                    {
                        count++;
                    }
                }
            }
            
            return Json(ApiResponse<BootstrapEmbeddingsDto>.Ok(new BootstrapEmbeddingsDto 
            { 
                Message = "Bootstrap Complete! " + count + " historical tickets indexed.", 
                TotalPoolSize = resolvedTicketIds.Count 
            }));
        }

        // ─── Diagnostic API ──────────────────────────────────────────
        [HttpGet("Tickets/DebugSemantic")]
        [AllowAnonymous]
        public async Task<IActionResult> DebugSemantic(string? switchProvider = null, bool resetDb = false)
        {
            if (resetDb)
            {
                var allVectorsInternal = await _context.TicketSemanticEmbeddings.ToListAsync();
                _context.TicketSemanticEmbeddings.RemoveRange(allVectorsInternal);
                await _context.SaveChangesAsync();
            }

            if (!string.IsNullOrEmpty(switchProvider))
            {
                var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "AiActiveProvider");
                if (setting != null) {
                    setting.Value = switchProvider;
                    _context.Update(setting);
                } else {
                    _context.SystemSettings.Add(new SystemSetting { Key = "AiActiveProvider", Value = switchProvider });
                }
                await _context.SaveChangesAsync();
            }

            var allVectors = await _context.TicketSemanticEmbeddings.ToListAsync();
            var systemSetting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "AiActiveProvider");
            
            var payload = new DebugSemanticDto
            {
                AiProviderDbSetting = systemSetting?.Value ?? "Unknown",
                TotalEmbeddingsInDb = allVectors.Count,
                VectorDetails = _mapper.Map<List<VectorDetailDto>>(allVectors)
            };
            return Json(ApiResponse<DebugSemanticDto>.Ok(payload));
        }
    }
}
