using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Models;
using AISupportAnalysisPlatform.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AISupportAnalysisPlatform.Controllers.Tickets
{
    [Authorize]
    public class CommentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notificationService;

        public CommentsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, INotificationService notificationService)
        {
            _context = context;
            _userManager = userManager;
            _notificationService = notificationService;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Add(int ticketId, string content, List<IFormFile> files)
        {
            if (string.IsNullOrWhiteSpace(content)) return RedirectToAction("Details", "Tickets", new { id = ticketId });
            
            var user = await _userManager.GetUserAsync(User);
            var comment = new TicketComment
            {
                TicketId = ticketId,
                Content = content,
                CreatedByUserId = user!.Id
            };
            
            _context.TicketComments.Add(comment);
            await _context.SaveChangesAsync();

            // Handle Comment Attachments
            if (files != null && files.Count > 0)
            {
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "comments");
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
                            TicketId = ticketId,
                            CommentId = comment.Id,
                            FileName = file.FileName,
                            FilePath = "/uploads/comments/" + uniqueFileName,
                            ContentType = file.ContentType,
                            FileSize = file.Length,
                            UploadedByUserId = user.Id
                        };
                        _context.TicketAttachments.Add(attachment);
                    }
                }
            }

            var ticket = await _context.Tickets.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket != null)
            {
                var isAdmin = await _userManager.IsInRoleAsync(user, RoleNames.Admin);
                
                if (isAdmin)
                {
                    await _notificationService.CreateNotificationAsync(ticket.CreatedByUserId, "Expert Intelligence Update", $"Technical Specialist added correspondence to Case {ticket.TicketNumber}.", $"/Tickets/Details/{ticketId}");
                }
                else if (!string.IsNullOrEmpty(ticket.AssignedToUserId))
                {
                    await _notificationService.CreateNotificationAsync(ticket.AssignedToUserId, "Customer Communication", $"The Entity Reporter has replied to Case {ticket.TicketNumber}.", $"/Tickets/Details/{ticketId}");
                }
            }
            
            _context.TicketHistories.Add(new TicketHistory {
                TicketId = ticketId,
                Action = "Correspondence Artifact Uploaded",
                UserId = comment.CreatedByUserId,
                CreatedAt = DateTime.UtcNow
            });
            
            await _context.SaveChangesAsync();
            return RedirectToAction("Details", "Tickets", new { id = ticketId });
        }
    }
}
