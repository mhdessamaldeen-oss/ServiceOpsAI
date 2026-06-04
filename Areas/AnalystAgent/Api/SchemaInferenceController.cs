namespace AnalystAgent.Api;

using Microsoft.AspNetCore.Mvc;
using AnalystAgent.Abstractions;
using AnalystAgent.Retrieval;
using AnalystAgent.Schema;

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
[Route("api/analyst-agent/schema-inference")]
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

    /// <summary>
    /// Status of the persisted schema-embedding index: which embedding model + schema hash the
    /// vectors were built with, how many, when, and whether they are STALE (model/schema changed
    /// since). Powers the "Generate Schema Embeddings" panel in Copilot settings.
    /// <c>GET /embeddings/status</c>.
    /// </summary>
    [HttpGet("embeddings/status")]
    public ActionResult EmbeddingsStatus(
        [FromServices] ISchemaEmbeddingStore store,
        [FromServices] ISchemaEmbeddingJobRunner job,
        [FromServices] ISchemaKnowledge knowledge,
        [FromServices] ITextEmbedder embedder)
    {
        var model = embedder.ModelName ?? "";
        var hash = knowledge.SchemaHash ?? "";
        var status = store.GetStatus(model, hash, new[] { SchemaEmbeddingStore.KindTables });
        return Ok(new
        {
            embeddingModel = model,
            schemaHash = hash,
            available = !string.IsNullOrWhiteSpace(model),
            job = job.State,
            kinds = status,
        });
    }

    /// <summary>
    /// Generate (or regenerate) the schema embeddings ONE TIME: clear the persisted index, then
    /// re-embed every visible table with the configured embedding model and save to disk. Called
    /// from the settings button and automatically at the end of the schema-inference job (when the
    /// schema changed). This is the only moment the embedding model needs to be loaded.
    /// <c>POST /embeddings/generate</c>.
    /// </summary>
    [HttpPost("embeddings/generate")]
    public ActionResult GenerateEmbeddings(
        [FromServices] ISchemaEmbeddingJobRunner job,
        [FromServices] ITextEmbedder embedder)
    {
        if (string.IsNullOrWhiteSpace(embedder.ModelName))
            return BadRequest(new { error = "no embedding model configured — set the Embedding workload model in AI settings first" });

        // Detached background job — embedding 59 tables is slow on a constrained GPU; a blocking
        // request would be killed by a client disconnect (and persist nothing). The UI polls status.
        var started = job.Start();
        return started
            ? Accepted(job.State)
            : Conflict(new { error = "generation already running", state = job.State });
    }
}
