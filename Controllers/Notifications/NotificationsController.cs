using AISupportAnalysisPlatform.Data;
using AISupportAnalysisPlatform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AISupportAnalysisPlatform.Models.DTOs;
using AISupportAnalysisPlatform.Models.Common;
using AutoMapper;
using AutoMapper.QueryableExtensions;

namespace AISupportAnalysisPlatform.Controllers.Notifications
{
    [Authorize]
    public class NotificationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IMapper _mapper;

        public NotificationsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IMapper mapper)
        {
            _context = context;
            _userManager = userManager;
            _mapper = mapper;
        }

        // GET: Notifications
        public async Task<IActionResult> Index()
        {
            var userId = _userManager.GetUserId(User);
            var notifs = await _context.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt)
                .ProjectTo<NotificationDto>(_mapper.ConfigurationProvider)
                .ToListAsync();
            return View(notifs);
        }

        [HttpPost]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var notif = await _context.Notifications.FindAsync(id);
            if (notif != null && notif.UserId == _userManager.GetUserId(User))
            {
                notif.IsRead = true;
                await _context.SaveChangesAsync();
                return Json(ApiResponse.Ok());
            }
            return BadRequest(ApiResponse.Fail("Notification not found or access denied"));
        }
    }
}
