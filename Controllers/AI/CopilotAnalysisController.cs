using ServiceOpsAI.Services.AI.Copilot.Analysis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ServiceOpsAI.Controllers.AI
{
    using static ServiceOpsAI.Services.AI.Copilot.Analysis.TraceDataInspector;
    [Authorize(Roles = "Admin")]
    [Route("AiAnalysis/[controller]/[action]")]
    public class CopilotAnalysisController : Controller
    {
        private readonly ICopilotTraceAnalyzer _analyzer;
        private readonly IAnswerCorrectnessAssessor _correctnessAssessor;
        private readonly ITraceDataInspector _dataInspector;
        private readonly ILogger<CopilotAnalysisController> _logger;

        public CopilotAnalysisController(
            ICopilotTraceAnalyzer analyzer,
            IAnswerCorrectnessAssessor correctnessAssessor,
            ITraceDataInspector dataInspector,
            ILogger<CopilotAnalysisController> logger)
        {
            _analyzer = analyzer;
            _correctnessAssessor = correctnessAssessor;
            _dataInspector = dataInspector;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> TraceInvestigation(int? sessionId = null)
        {
            var report = await _analyzer.AnalyzeRecentTracesAsync(100, sessionId);
            var patterns = await _analyzer.IdentifyPatternsAsync(sessionId);
            var anomalies = await _analyzer.FindAnomaliesAsync(sessionId);
            var intentDist = await _analyzer.GetIntentDistributionAsync(sessionId);


            ViewBag.Report = report;
            ViewBag.Patterns = patterns;
            ViewBag.Anomalies = anomalies;
            ViewBag.IntentDistribution = intentDist;

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> ApiReport()
        {
            try
            {
                var report = await _analyzer.AnalyzeRecentTracesAsync(100);
                return Json(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate trace analysis report");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ApiTraceInvestigationReport(int? sessionId = null)
        {
            try
            {
                var report = await _analyzer.AnalyzeRecentTracesAsync(100, sessionId);
                var patterns = await _analyzer.IdentifyPatternsAsync(sessionId);
                var anomalies = await _analyzer.FindAnomaliesAsync(sessionId);
                var intentDist = await _analyzer.GetIntentDistributionAsync(sessionId);

                
                return Json(new
                {
                    Report = report,
                    Patterns = patterns,
                    Anomalies = anomalies,
                    IntentDistribution = intentDist
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate trace investigation report");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> CorrectnessReport(int? sessionId = null)
        {
            var report = await _correctnessAssessor.GenerateCorrectnessReportAsync(100, sessionId);
            return View(report);
        }


        [HttpGet]
        public async Task<IActionResult> ApiCorrectnessReport(int? sessionId = null)
        {
            try
            {
                var report = await _correctnessAssessor.GenerateCorrectnessReportAsync(100, sessionId);
                return Json(report);
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate correctness report");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AssessTrace(int id)
        {
            try
            {
                var assessment = await _correctnessAssessor.AssessAutomaticallyAsync(id);
                return Json(assessment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assess trace {TraceId}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ReviewTrace([FromBody] ManualReviewRequest request)
        {
            try
            {
                await _correctnessAssessor.RecordManualReviewAsync(
                    request.TraceId,
                    request.IsCorrect,
                    request.Notes,
                    request.IssueCategories.ToArray(),
                    User.Identity?.Name ?? "Unknown");
                
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record review for trace {TraceId}", request.TraceId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── TRACE DATA INSPECTION ─────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> InspectData(int count = 50, int? sessionId = null)
        {
            try
            {
                var traces = await _dataInspector.InspectRecentTracesAsync(count, sessionId);
                var analysis = await _dataInspector.AnalyzeCorrectnessPatternsAsync(count, sessionId);
                
                ViewBag.Traces = traces;
                ViewBag.Analysis = analysis;
                
                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect trace data");
                return View("Error", new { message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ApiInspectData(int count = 50, int? sessionId = null)
        {
            try
            {
                var analysis = await _dataInspector.AnalyzeCorrectnessPatternsAsync(count, sessionId);
                return Json(analysis);
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect trace data");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> QuestionAnswerPairs(int count = 50, int? sessionId = null)
        {
            try
            {
                var pairs = await _dataInspector.GetQuestionAnswerPairsAsync(count, sessionId);
                return Json(pairs);
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get question-answer pairs");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> InspectSingle(int id)
        {
            try
            {
                var trace = await _dataInspector.InspectSingleTraceAsync(id);
                if (trace == null)
                    return NotFound();
                
                return Json(trace);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect trace {TraceId}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ApiUnifiedForensicReport(int? sessionId = null)
        {
            try
            {
                // 1. Trace Investigation
                var analyzerReport = await _analyzer.AnalyzeRecentTracesAsync(100, sessionId);
                var patterns = await _analyzer.IdentifyPatternsAsync(sessionId);
                var anomalies = await _analyzer.FindAnomaliesAsync(sessionId);
                var intentDist = await _analyzer.GetIntentDistributionAsync(sessionId);

                // 2. Correctness
                var correctnessReport = await _correctnessAssessor.GenerateCorrectnessReportAsync(100, sessionId);

                // 3. Data Inspection
                var dataAnalysis = await _dataInspector.AnalyzeCorrectnessPatternsAsync(100, sessionId);

                return Json(new
                {
                    Investigation = new {
                        Patterns = patterns,
                        Anomalies = anomalies,
                        IntentDistribution = intentDist,
                        Traces = analyzerReport.Traces // Expose traces for the UI grid
                    },
                    Correctness = correctnessReport,
                    DataInspection = dataAnalysis
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate unified forensic report");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    public class ManualReviewRequest
    {
        public int TraceId { get; set; }
        public bool IsCorrect { get; set; }
        public string Notes { get; set; } = "";
        public List<string> IssueCategories { get; set; } = new List<string>();
    }
}
