using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Models.Common;
using ServiceOpsAI.Models.DTOs;
using AutoMapper;
using AutoMapper.QueryableExtensions;

namespace ServiceOpsAI.Controllers.Users
{
    [Authorize(Roles = RoleNames.Admin)]
    public class UsersController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public UsersController(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, ApplicationDbContext context, IMapper mapper)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _mapper = mapper;
        }

        [AllowAnonymous]
        public async Task<IActionResult> Index([FromQuery] GridRequestModel request, bool seeAllUsers = false)
        {
            request.Normalize();

            if (!seeAllUsers && !User.IsInRole(RoleNames.Admin))
            {
                return Challenge();
            }
            var usersQuery = _userManager.Users
                .AsNoTracking()
                .Include(u => u.Department)
                .AsQueryable();

            if (!string.IsNullOrEmpty(request.SearchString))
            {
                usersQuery = usersQuery.Where(u => (u.Email ?? string.Empty).Contains(request.SearchString) || 
                                                (u.FirstName ?? string.Empty).Contains(request.SearchString) || 
                                                (u.LastName ?? string.Empty).Contains(request.SearchString));
            }

            if (request.DepartmentId.HasValue)
            {
                usersQuery = usersQuery.Where(u => u.DepartmentId == request.DepartmentId);
            }

            switch (request.SortOrder)
            {
                case "email_desc":
                    usersQuery = usersQuery.OrderByDescending(u => u.Email ?? string.Empty);
                    break;
                case "Name":
                    usersQuery = usersQuery.OrderBy(u => u.FirstName ?? string.Empty).ThenBy(u => u.LastName ?? string.Empty);
                    break;
                case "name_desc":
                    usersQuery = usersQuery.OrderByDescending(u => u.FirstName ?? string.Empty).ThenByDescending(u => u.LastName ?? string.Empty);
                    break;
                case "Department":
                    usersQuery = usersQuery.OrderBy(u => u.Department != null ? u.Department.Name : string.Empty);
                    break;
                case "entity_desc":
                    usersQuery = usersQuery.OrderByDescending(u => u.Department != null ? u.Department.Name : string.Empty);
                    break;
                default:
                    usersQuery = usersQuery.OrderBy(u => u.Email ?? string.Empty);
                    break;
            }

            var totalCount = await usersQuery.CountAsync();
            var effectivePageSize = request.GetEffectivePageSize(totalCount);
            var items = await usersQuery.Skip((request.PageNumber - 1) * effectivePageSize)
                                        .Take(effectivePageSize)
                                        .ProjectTo<UserDto>(_mapper.ConfigurationProvider)
                                        .ToListAsync();

            var pagedResult = new PagedResult<UserDto>
            {
                Items = items,
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = effectivePageSize,
                Request = request
            };

            ViewData["EntityFilterId"] = new SelectList(_context.Departments, "Id", "Name", request.DepartmentId);
            return View(pagedResult);
        }

        public IActionResult Create()
        {
            ViewData["DepartmentId"] = new SelectList(_context.Departments, "Id", "Name");
            ViewData["Roles"] = new SelectList(_roleManager.Roles, "Name", "Name");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Email,FirstName,LastName,DepartmentId")] ApplicationUser user, string password, string role, bool isAdmin = false)
        {
            if (ModelState.IsValid)
            {
                user.UserName = user.Email;
                user.EmailConfirmed = true;
                
                var result = await _userManager.CreateAsync(user, password);
                if (result.Succeeded)
                {
                    if (isAdmin)
                    {
                        await _userManager.AddToRoleAsync(user, RoleNames.Admin);
                    }
                    else if (!string.IsNullOrEmpty(role))
                    {
                        await _userManager.AddToRoleAsync(user, role);
                    }
                    return RedirectToAction(nameof(Index));
                }
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
            }
            ViewData["DepartmentId"] = new SelectList(_context.Departments, "Id", "Name", user.DepartmentId);
            ViewData["Roles"] = new SelectList(_roleManager.Roles, "Name", "Name", role);
            return View(user);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (id == null) return NotFound();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            ViewData["DepartmentId"] = new SelectList(_context.Departments, "Id", "Name", user.DepartmentId);
            var roles = await _userManager.GetRolesAsync(user);
            ViewBag.CurrentRole = roles.FirstOrDefault();
            ViewData["Roles"] = new SelectList(_roleManager.Roles, "Name", "Name", ViewBag.CurrentRole);

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,FirstName,LastName,DepartmentId,IsActive")] ApplicationUser user, string role, bool isAdmin = false)
        {
            if (id != user.Id) return NotFound();

            var existingUser = await _userManager.FindByIdAsync(id);
            if (existingUser == null) return NotFound();

            existingUser.FirstName = user.FirstName;
            existingUser.LastName = user.LastName;
            existingUser.DepartmentId = user.DepartmentId;
            existingUser.IsActive = user.IsActive;

            var result = await _userManager.UpdateAsync(existingUser);
            if (result.Succeeded)
            {
                var userRoles = await _userManager.GetRolesAsync(existingUser);
                var targetRole = isAdmin ? RoleNames.Admin : role;
                if (userRoles.FirstOrDefault() != targetRole)
                {
                    if (userRoles.Any())
                    {
                        await _userManager.RemoveFromRolesAsync(existingUser, userRoles);
                    }
                    if (!string.IsNullOrEmpty(targetRole))
                    {
                        await _userManager.AddToRoleAsync(existingUser, targetRole);
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            
            ViewData["DepartmentId"] = new SelectList(_context.Departments, "Id", "Name", user.DepartmentId);
            ViewData["Roles"] = new SelectList(_roleManager.Roles, "Name", "Name", role);
            return View(user);
        }
    }
}
