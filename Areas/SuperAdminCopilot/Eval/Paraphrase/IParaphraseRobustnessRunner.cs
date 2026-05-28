namespace SuperAdminCopilot.Eval.Paraphrase;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Copilot.Assessment;
using SuperAdminCopilot.HostBridge;
using SuperAdminCopilot.Eval; // for IExecutionAccuracyChecker (parent namespace)

/// <summary>
/// Runs a <see cref="ParaphraseSuite"/> through the live copilot pipeline and aggregates
/// per-perturbation + per-cluster accuracy. Reads the same flat <c>Scenarios[]</c> shape
/// the existing <c>CopilotAssessmentHandler</c> consumes, so suite files can live in
/// <c>Configuration/QuestionSuites/</c> alongside every other suite — no separate folder,
/// no fork of the loader.
///
/// <para><b>Cluster grouping happens at report-build time.</b> Each scenario carries its
/// own <see cref="ParaphraseScenario.ClusterId"/>; the runner runs scenarios independently
/// then groups results by cluster id to compute cluster-level metrics. Scenarios that omit
/// a <c>ClusterId</c> aggregate into the synthetic <c>"unclustered"</c> bucket.</para>
///
/// <para><b>Not registered in DI by default.</b> Add the registration in
/// <c>ServiceCollectionExtensions</c> when ready — until then this class is dormant code.</para>
/// </summary>
public interface IParaphraseRobustnessRunner
{
    /// <summary>Run the entire suite at <paramref name="suiteFilePath"/>. Returns the
    /// aggregated report.</summary>
    Task<ParaphraseRobustnessReport> RunAsync(
        string suiteFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>Run a single scenario. Useful for triaging a regression on one phrasing.</summary>
    Task<ScenarioResult> RunScenarioAsync(
        ParaphraseScenario scenario,
        CancellationToken cancellationToken = default);
}

internal sealed class ParaphraseRobustnessRunner : IParaphraseRobustnessRunner
{
    private readonly ISuperAdminCopilotChatBridge _bridge;
    private readonly IExecutionAccuracyChecker _exChecker;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly ILogger<ParaphraseRobustnessRunner> _logger;

    // Mirrors the JsonSerializerOptions used by CopilotAssessmentHandler so the suite files
    // round-trip identically — case-insensitive property names, comments allowed, enums as strings.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public ParaphraseRobustnessRunner(
        ISuperAdminCopilotChatBridge bridge,
        IExecutionAccuracyChecker exChecker,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        ILogger<ParaphraseRobustnessRunner> logger)
    {
        _bridge = bridge;
        _exChecker = exChecker;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<ParaphraseRobustnessReport> RunAsync(
        string suiteFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(suiteFilePath))
            throw new FileNotFoundException($"Paraphrase suite file not found: {suiteFilePath}");

        var suiteJson = await File.ReadAllTextAsync(suiteFilePath, cancellationToken);
        var suite = JsonSerializer.Deserialize<ParaphraseSuite>(suiteJson, JsonOptions)
            ?? throw new InvalidOperationException($"Suite file {suiteFilePath} parsed to null");

        var startedUtc = DateTime.UtcNow;
        _logger.LogInformation(
            "[ParaphraseEval] starting suite '{Suite}' — {Total} scenarios",
            suite.Name, suite.Scenarios.Count);

        // Per user direction 2026-05-25: one CopilotChatSession per suite file. Without
        // this, every paraphrase scenario built a CopilotChatRequest with a null SessionId,
        // and every trace landed in CopilotTraceHistories with SessionId=NULL — looked
        // "session-less" in the grid. Title = the suite file stem so the dropdown picks
        // it up alongside regular assessment sessions.
        var suiteTitle = string.IsNullOrWhiteSpace(suite.Name)
            ? Path.GetFileNameWithoutExtension(suiteFilePath)
            : suite.Name;
        int? sessionId = null;
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var chatSession = CopilotAssessmentHandler.CreateNewAssessmentSession(userId: null);
            chatSession.Title = suiteTitle;
            chatSession.Surface = "paraphrase-eval";
            ctx.CopilotChatSessions.Add(chatSession);
            await ctx.SaveChangesAsync(cancellationToken);
            sessionId = chatSession.Id;
        }
        catch (Exception ex)
        {
            // Session creation is best-effort — if the DB write fails for any reason we
            // still want the eval run to proceed (scenarios will fall back to null sessionId,
            // matching the pre-2026-05-25 behaviour for any individual scenario).
            _logger.LogWarning(ex, "[ParaphraseEval] failed to create suite session — scenarios will run without SessionId");
        }

        var pairs = new List<(ParaphraseScenario Scenario, ScenarioResult Result)>(suite.Scenarios.Count);
        foreach (var scenario in suite.Scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunScenarioAsync(scenario, sessionId, cancellationToken);
            pairs.Add((scenario, result));
        }

        var completedUtc = DateTime.UtcNow;
        var report = BuildReport(suite.Name, startedUtc, completedUtc, pairs);
        _logger.LogInformation(
            "[ParaphraseEval] finished suite '{Suite}' — overall pass {Pass:P1}, worst perturbation '{WorstName}' drop {Drop:F1}pp",
            suite.Name, report.OverallPassRate, report.WorstPerturbation, report.WorstPerturbationDropPct);
        return report;
    }

    public Task<ScenarioResult> RunScenarioAsync(
        ParaphraseScenario scenario,
        CancellationToken cancellationToken = default)
        => RunScenarioAsync(scenario, sessionId: null, cancellationToken);

    public async Task<ScenarioResult> RunScenarioAsync(
        ParaphraseScenario scenario,
        int? sessionId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new CopilotChatRequest
            {
                Question = scenario.Question,
                Surface = "paraphrase-eval",
                IsAssessment = true,
                ExpectedSql = scenario.ExpectedSql,
                CaseCode = scenario.Code,
                SourceSuite = "paraphrase-robustness",
                SessionId = sessionId,
            };

            var response = await _bridge.AskAsync(request, cancellationToken);
            stopwatch.Stop();

            // Same conversion pattern as CopilotAssessmentHandler — flatten StructuredRows.Values
            // into the IReadOnlyDictionary shape the checker expects.
            var copilotRows = response.StructuredRows?
                .Select(r => (IReadOnlyDictionary<string, object?>)r.Values
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var exResult = await _exChecker.CheckAsync(scenario.ExpectedSql, copilotRows, cancellationToken);

            var passed = exResult.Match == true;
            var reason = passed
                ? null
                : exResult.Error
                    ?? (exResult.Match == false ? "result-set mismatch" : "no copilot rows / inconclusive");

            return new ScenarioResult
            {
                Code = scenario.Code,
                Question = scenario.Question,
                Perturbation = string.IsNullOrWhiteSpace(scenario.Perturbation) ? "unlabeled" : scenario.Perturbation,
                ClusterId = string.IsNullOrWhiteSpace(scenario.ClusterId) ? "unclustered" : scenario.ClusterId,
                Passed = passed,
                FailureReason = reason,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                ExpectedRowCount = exResult.ExpectedRowCount,
                ActualRowCount = exResult.CopilotRowCount,
                GeneratedSql = response.ExecutionDetails?.LastTechnicalData,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex,
                "[ParaphraseEval] scenario {Code} threw — counted as errored", scenario.Code);
            return new ScenarioResult
            {
                Code = scenario.Code,
                Question = scenario.Question,
                Perturbation = string.IsNullOrWhiteSpace(scenario.Perturbation) ? "unlabeled" : scenario.Perturbation,
                ClusterId = string.IsNullOrWhiteSpace(scenario.ClusterId) ? "unclustered" : scenario.ClusterId,
                Passed = false,
                FailureReason = $"exception: {ex.GetType().Name}: {ex.Message}",
                LatencyMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }

    private static ParaphraseRobustnessReport BuildReport(
        string suiteName,
        DateTime startedUtc,
        DateTime completedUtc,
        IReadOnlyList<(ParaphraseScenario Scenario, ScenarioResult Result)> pairs)
    {
        var total = pairs.Count;
        var passed = pairs.Count(p => p.Result.Passed);
        var overallPassRate = total == 0 ? 0 : (double)passed / total;

        // Per-perturbation aggregation.
        var perturbationReports = pairs
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Result.Perturbation) ? "unlabeled" : p.Result.Perturbation)
            .Select(g =>
            {
                var pTotal = g.Count();
                var pPassed = g.Count(x => x.Result.Passed);
                var pErrored = g.Count(x => x.Result.FailureReason?.StartsWith("exception:", StringComparison.Ordinal) == true);
                return new PerturbationReport
                {
                    Perturbation = g.Key,
                    Total = pTotal,
                    Passed = pPassed,
                    Failed = pTotal - pPassed - pErrored,
                    Errored = pErrored,
                    PassRate = pTotal == 0 ? 0 : (double)pPassed / pTotal,
                };
            })
            .OrderBy(p => p.PassRate)
            .ToList();

        // Per-cluster aggregation — groups by ClusterId, takes Category from the first
        // scenario in each cluster (clusters share Category by construction).
        var clusterResults = pairs
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Scenario.ClusterId) ? "unclustered" : p.Scenario.ClusterId)
            .Select(g =>
            {
                var cTotal = g.Count();
                var cPassed = g.Count(x => x.Result.Passed);
                var firstScenario = g.First().Scenario;
                return new ClusterResult
                {
                    ClusterId = g.Key,
                    Category = firstScenario.Category,
                    Total = cTotal,
                    Passed = cPassed,
                    PassRate = cTotal == 0 ? 0 : (double)cPassed / cTotal,
                    Scenarios = g.Select(x => x.Result).ToList(),
                };
            })
            .OrderBy(c => c.PassRate)
            .ToList();

        // Dr.Spider headline metric: worst per-perturbation drop relative to the 'base'
        // perturbation. If the suite has no 'base' perturbation, fall back to comparing each
        // perturbation against the overall pass rate.
        var basePerturbation = perturbationReports.FirstOrDefault(p => p.Perturbation == "base");
        var baseline = basePerturbation?.PassRate ?? overallPassRate;
        var worst = perturbationReports
            .Where(p => p.Perturbation != "base")
            .OrderBy(p => p.PassRate)
            .FirstOrDefault();
        var worstDropPct = worst is null ? 0 : Math.Max(0, (baseline - worst.PassRate) * 100.0);

        return new ParaphraseRobustnessReport
        {
            SuiteName = suiteName,
            RunStartedUtc = startedUtc,
            RunCompletedUtc = completedUtc,
            TotalScenarios = total,
            TotalClusters = clusterResults.Count,
            OverallPassRate = overallPassRate,
            WorstPerturbationDropPct = worstDropPct,
            WorstPerturbation = worst?.Perturbation ?? "",
            ByPerturbation = perturbationReports,
            ByCluster = clusterResults,
        };
    }
}
