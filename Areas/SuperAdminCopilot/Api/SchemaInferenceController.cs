namespace SuperAdminCopilot.Api;

using Microsoft.AspNetCore.Mvc;
using SuperAdminCopilot.Retrieval;
using SuperAdminCopilot.Schema;

/// <summary>
/// User-triggered schema inference operations for the Copilot admin UI.
/// <list type="bullet">
///   <item>GET  /status   — current job state + file metadata (poll for progress)</item>
///   <item>POST /generate — start a background generation job (idempotent: returns false if already running)</item>
///   <item>DELETE /       — remove the schema-inferred.json file</item>
///   <item>GET  /preview  — return the raw JSON file content for inspection</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/super-admin-copilot/schema-inference")]
public sealed class SchemaInferenceController : ControllerBase
{
    private readonly ISchemaInferenceJobRunner _runner;

    public SchemaInferenceController(ISchemaInferenceJobRunner runner) => _runner = runner;

    [HttpGet("status")]
    public ActionResult<SchemaInferenceJobState> GetStatus() => Ok(_runner.State);

    [HttpPost("generate")]
    public async Task<ActionResult> Generate(CancellationToken ct)
    {
        var started = await _runner.StartAsync(ct);
        return started
            ? Accepted(_runner.State)
            : Conflict(new { error = "generation already running", state = _runner.State });
    }

    [HttpDelete]
    public ActionResult Delete()
    {
        var deleted = _runner.Delete();
        return deleted
            ? Ok(new { deleted = true })
            : NotFound(new { deleted = false, reason = "file did not exist" });
    }

    [HttpGet("preview")]
    public async Task<ActionResult> Preview(CancellationToken ct)
    {
        var content = await _runner.ReadFileContentAsync(ct);
        if (content is null) return NotFound(new { error = "schema-inferred.json not generated yet" });
        return Content(content, "application/json");
    }

    /// <summary>
    /// Debug helper: returns the top-K tables the semantic retriever picks for a question.
    /// Helps diagnose why a question routes to a particular table without running the full
    /// pipeline. <c>GET /retrieve?q=show+me+users</c>.
    /// </summary>
    [HttpGet("retrieve")]
    public async Task<ActionResult> Retrieve(
        [FromQuery] string q,
        [FromQuery] int topK,
        [FromServices] ISchemaSemanticRetriever retriever,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "query 'q' is required" });
        if (topK <= 0) topK = 3;
        var result = await retriever.RetrieveAsync(q, topK, ct);
        return Ok(new
        {
            available = retriever.IsAvailable,
            question = q,
            matches = result.Tables.Select(m => new { table = m.Table.Name, score = m.Score }),
        });
    }
}
