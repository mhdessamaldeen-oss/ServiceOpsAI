namespace ServiceOpsAI.Controllers.AI
{
    using System.Text.Json;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using ServiceOpsAI.Constants;
    using SuperAdminCopilot.Eval.Paraphrase;

    /// <summary>
    /// Phase 0 — admin entry point for the paraphrase-robustness evaluation. Lists
    /// paraphrase-suite files from <c>Areas/SuperAdminCopilot/Configuration/QuestionSuites/</c>
    /// (the same folder the live <c>CopilotAssessmentHandler</c> scans — paraphrase suites
    /// match the standard flat <c>Scenarios[]</c> shape so both runners read the same files)
    /// and runs a chosen suite through the live copilot pipeline. Returns the structured
    /// report so the admin UI / curl can see per-perturbation pass rates.
    ///
    /// <para><b>Discovery filter:</b> the controller only lists suite files whose top-level
    /// <c>Scenarios[]</c> contains at least one entry with a non-empty <c>ClusterId</c> —
    /// the marker that distinguishes paraphrase suites from regular assessment suites. This
    /// keeps the standard catalog from cluttering the paraphrase-eval dropdown.</para>
    ///
    /// <para><b>Read-only on the database side.</b> The eval runner uses the same
    /// read-only copilot path the chat UI uses; no writes are produced. Safe to run while
    /// the live assessment is in progress — the only resource shared is the read-only DB.</para>
    /// </summary>
    [Authorize(Roles = RoleNames.Admin)]
    [Route("ParaphraseEval/[action]")]
    public class ParaphraseEvalController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly IParaphraseRobustnessRunner _runner;
        private readonly ILogger<ParaphraseEvalController> _logger;

        private static readonly JsonSerializerOptions ReportJson = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public ParaphraseEvalController(
            IWebHostEnvironment env,
            IParaphraseRobustnessRunner runner,
            ILogger<ParaphraseEvalController> logger)
        {
            _env = env;
            _runner = runner;
            _logger = logger;
        }

        /// <summary>Enumerate paraphrase suites under <c>Configuration/QuestionSuites/</c> —
        /// filtered to files whose scenarios carry a <c>ClusterId</c>, distinguishing
        /// paraphrase suites from regular assessment suites that live in the same folder.</summary>
        [HttpGet]
        public IActionResult Suites()
        {
            var folder = ResolveSuitesFolder();
            if (!Directory.Exists(folder))
                return Ok(new { Folder = folder, Suites = Array.Empty<object>() });

            var suites = Directory.GetFiles(folder, "*.json")
                .Where(IsParaphraseSuite)
                .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
                .Select(f => new
                {
                    File = Path.GetFileName(f),
                    Path = f,
                    SizeBytes = new FileInfo(f).Length,
                    ModifiedUtc = System.IO.File.GetLastWriteTimeUtc(f),
                })
                .ToList();
            return Ok(new { Folder = folder, Suites = suites });
        }

        // Cheap probe — just read the file and check whether any scenario has a non-empty
        // ClusterId. Avoids deserialising into our full type since we only need the marker.
        // False on parse failure (treat non-suite JSON as "not paraphrase").
        private static bool IsParaphraseSuite(string path)
        {
            try
            {
                using var stream = System.IO.File.OpenRead(path);
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("Scenarios", out var scenarios)
                    && !doc.RootElement.TryGetProperty("scenarios", out scenarios))
                    return false;
                foreach (var s in scenarios.EnumerateArray())
                {
                    if ((s.TryGetProperty("ClusterId", out var cid)
                         || s.TryGetProperty("clusterId", out cid))
                        && cid.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(cid.GetString()))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Run a paraphrase suite. The suite identifier is the FILENAME under
        /// <c>Eval/Suites/</c> — not a full path, to prevent directory traversal.</summary>
        [HttpPost]
        public async Task<IActionResult> Run(string suiteFile, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(suiteFile))
                return BadRequest(new { Error = "suiteFile is required" });

            // Defence against path traversal: reject anything that isn't a plain filename
            // ending in .json.
            if (suiteFile.Contains("..", StringComparison.Ordinal)
                || suiteFile.Contains('/', StringComparison.Ordinal)
                || suiteFile.Contains('\\', StringComparison.Ordinal)
                || !suiteFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { Error = "suiteFile must be a plain .json filename within the suites folder" });

            var fullPath = Path.Combine(ResolveSuitesFolder(), suiteFile);
            if (!System.IO.File.Exists(fullPath))
                return NotFound(new { Error = $"Suite file not found: {suiteFile}" });

            _logger.LogInformation("[ParaphraseEval] starting suite {Suite}", suiteFile);

            try
            {
                var report = await _runner.RunAsync(fullPath, cancellationToken);

                // Side-effect: persist the report next to the suites folder so subsequent runs
                // can be compared offline. Filename includes a UTC timestamp so reports stack
                // chronologically.
                await TryPersistReportAsync(suiteFile, report, cancellationToken);

                return Ok(report);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[ParaphraseEval] suite {Suite} was cancelled", suiteFile);
                return StatusCode(499, new { Error = "Run cancelled by client" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ParaphraseEval] suite {Suite} threw", suiteFile);
                return StatusCode(500, new { Error = ex.Message, Type = ex.GetType().Name });
            }
        }

        /// <summary>List previously-persisted reports for the given suite.</summary>
        [HttpGet]
        public IActionResult Reports(string? suiteFile = null)
        {
            var folder = ResolveReportsFolder();
            if (!Directory.Exists(folder))
                return Ok(new { Folder = folder, Reports = Array.Empty<object>() });

            var pattern = string.IsNullOrWhiteSpace(suiteFile)
                ? "*.json"
                : $"{Path.GetFileNameWithoutExtension(suiteFile)}-*.json";

            var reports = Directory.GetFiles(folder, pattern)
                .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
                .Select(f => new
                {
                    File = Path.GetFileName(f),
                    Path = f,
                    SizeBytes = new FileInfo(f).Length,
                    ModifiedUtc = System.IO.File.GetLastWriteTimeUtc(f),
                })
                .Take(50)
                .ToList();
            return Ok(new { Folder = folder, Reports = reports });
        }

        // Paraphrase suites live alongside every other assessment suite — same folder,
        // same shape. The Suites() endpoint above filters for the ClusterId marker so the
        // standard catalog isn't surfaced through this controller.
        private string ResolveSuitesFolder() => Path.Combine(
            _env.ContentRootPath, "Areas", "SuperAdminCopilot", "Configuration", "QuestionSuites");

        // Reports get their own folder under Eval/ — they're paraphrase-runner artifacts,
        // not suite definitions.
        private string ResolveReportsFolder() => Path.Combine(
            _env.ContentRootPath, "Areas", "SuperAdminCopilot", "Eval", "Reports");

        private async Task TryPersistReportAsync(string suiteFile, ParaphraseRobustnessReport report, CancellationToken ct)
        {
            try
            {
                var folder = ResolveReportsFolder();
                Directory.CreateDirectory(folder);
                var stem = Path.GetFileNameWithoutExtension(suiteFile);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var path = Path.Combine(folder, $"{stem}-{stamp}.json");
                var json = JsonSerializer.Serialize(report, ReportJson);
                await System.IO.File.WriteAllTextAsync(path, json, ct);
                _logger.LogInformation("[ParaphraseEval] report persisted at {Path}", path);
            }
            catch (Exception ex)
            {
                // Persistence failure is non-fatal — the report already returned to the caller.
                _logger.LogWarning(ex, "[ParaphraseEval] failed to persist report — continuing");
            }
        }
    }
}
