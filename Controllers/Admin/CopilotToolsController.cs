using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Services.AI;
using AISupportAnalysisPlatform.Services.AI.Copilot.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AISupportAnalysisPlatform.Models.DTOs;
using AutoMapper;

namespace AISupportAnalysisPlatform.Controllers.Admin
{
    [Authorize(Roles = RoleNames.Admin)]
    public class CopilotToolsController : Controller
    {
        private readonly CopilotToolRegistry _toolRegistry;
        private readonly ILogger<CopilotToolsController> _logger;
        private readonly IMapper _mapper;

        public CopilotToolsController(CopilotToolRegistry toolRegistry, ILogger<CopilotToolsController> logger, IMapper mapper)
        {
            _toolRegistry = toolRegistry;
            _logger = logger;
            _mapper = mapper;
        }

        public async Task<IActionResult> Index()
        {
            var tools = await _toolRegistry.GetAllToolsAsync();
            var dtos = _mapper.Map<List<CopilotToolDto>>(tools);
            return View(dtos);
        }

        public IActionResult Create()
        {
            return View(new CopilotToolDefinition { IsEnabled = true, ToolType = "External", CopilotMode = "ExternalUtility" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CopilotToolDefinition tool)
        {
            if (ModelState.IsValid)
            {
                await _toolRegistry.SaveAsync(tool);
                return RedirectToAction(nameof(Index));
            }
            return View(tool);
        }

        public async Task<IActionResult> Edit(int id)
        {
            var tool = await _toolRegistry.GetByIdAsync(id);
            if (tool == null) return NotFound();
            return View(tool);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(CopilotToolDefinition tool)
        {
            if (ModelState.IsValid)
            {
                await _toolRegistry.SaveAsync(tool);
                return RedirectToAction(nameof(Index));
            }
            return View(tool);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id, bool isEnabled)
        {
            await _toolRegistry.ToggleAsync(id, isEnabled);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            await _toolRegistry.DeleteAsync(id);
            return RedirectToAction(nameof(Index));
        }
    }
}
