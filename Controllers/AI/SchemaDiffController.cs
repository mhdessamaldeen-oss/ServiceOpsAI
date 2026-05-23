using Microsoft.AspNetCore.Mvc;
using SuperAdminCopilot.Schema;

namespace AISupportAnalysisPlatform.Controllers.AI;

// Phase 6 Step 21 — schema diff endpoint for the admin UI.
//
// Returns the live-vs-cached schema diff as JSON so an admin page can render added /
// removed tables + columns. Backend-authoritative: the JSON shape is the source of
// truth, the UI just iterates the lists.
//
// Companion view: Views/Settings/SchemaDiff.cshtml (added in this commit) renders the
// report with re-sync guidance.
[ApiController]
[Route("api/[controller]")]
public sealed class SchemaDiffController : ControllerBase
{
    private readonly ISchemaDiffService _diff;

    public SchemaDiffController(ISchemaDiffService diff) => _diff = diff;

    // GET /api/schemadiff
    // Returns the live DB schema vs the cached schema-inferred.json diff.
    [HttpGet]
    public IActionResult Get()
    {
        try
        {
            return Ok(_diff.Compute());
        }
        catch (Exception ex)
        {
            return Problem($"Schema diff failed: {ex.Message}");
        }
    }
}
