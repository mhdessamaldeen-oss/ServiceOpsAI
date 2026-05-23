using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AISupportAnalysisPlatform.Controllers.Tickets
{
    [Authorize]
    public class AttachmentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public AttachmentsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(int ticketId, IFormFile file)
        {
            if (file != null && file.Length > 0)
            {
                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                var uniqueFileName = Guid.NewGuid().ToString() + "_" + file.FileName;
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var attachment = new TicketAttachment
                {
                    TicketId = ticketId,
                    FileName = file.FileName,
                    FilePath = "/uploads/" + uniqueFileName,
                    ContentType = file.ContentType,
                    FileSize = file.Length,
                    UploadedByUserId = _userManager.GetUserId(User)!
                };
                
                _context.TicketAttachments.Add(attachment);
                
                _context.TicketHistories.Add(new TicketHistory {
                    TicketId = ticketId,
                    Action = "Attachment Uploaded",
                    UserId = attachment.UploadedByUserId
                });

                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Details", "Tickets", new { id = ticketId });
        }

        [HttpGet]
        public async Task<IActionResult> Download(int id)
        {
            var attachment = await _context.TicketAttachments.FindAsync(id);
            if (attachment == null) return NotFound();

            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", attachment.FilePath.TrimStart('/'));
            if (!System.IO.File.Exists(filePath)) return NotFound();

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            return File(stream, attachment.ContentType, attachment.FileName);
        }
    }
}
