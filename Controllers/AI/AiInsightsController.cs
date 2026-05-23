using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Services.AI;
using AISupportAnalysisPlatform.Models.AI;
using AISupportAnalysisPlatform.Models.Common;

namespace AISupportAnalysisPlatform.Controllers.AI
{
    [Authorize(Roles = RoleNames.Admin)]
    public class AiInsightsController : Controller
    {
        private readonly IAiInsightsService _insightsService;

        public AiInsightsController(IAiInsightsService insightsService)
        {
            _insightsService = insightsService;
        }

        public async Task<IActionResult> Index([FromQuery] AiInsightsFilter filter)
        {
            var viewModel = await _insightsService.GetDashboardAsync(filter);

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                return PartialView("_ReviewAttentionGrid", viewModel);
            }

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteReview(int analysisId, string notes)
        {
            var success = await _insightsService.CompleteReviewAsync(analysisId, notes, User.Identity?.Name ?? "Admin");
            if (success)
            {
                return Json(ApiResponse.Ok());
            }
            return BadRequest(ApiResponse.Fail("Failed to complete review"));
        }
    }
}
