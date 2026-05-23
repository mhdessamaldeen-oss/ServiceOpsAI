namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

// Phase 3 Step 12 — Question Shape Classifier.
//
// Reads the user's question BEFORE the SpecExtractor and predicts the SQL shape.
// Most questions are "Simple" — the form-filling pipeline handles them well. A small
// fraction are "Complex" — window functions (running total, rank, percentile, lag/lead),
// recursive CTEs, or analytics the QuerySpec model can't express. For those we route
// straight to the LlmDirectSqlEmitter escape valve instead of wasting LLM calls on a
// form-filling attempt that will fail and trigger the escape valve anyway via retry.
//
// Implementation is deterministic regex + keyword (no LLM call). Patterns come from
// CopilotTextCatalog.QuestionShapeHints so operators can extend without recompiling.
public interface IQuestionShapeClassifier
{
    QuestionShape Classify(string question);
}

public enum QuestionShape
{
    Simple,           // standard form-filling QuerySpec handles it
    ComplexAnalytics  // window function / running total / rank / median / recursive — route to escape valve
}

internal sealed class QuestionShapeClassifier : IQuestionShapeClassifier
{
    private readonly IOptionsMonitor<CopilotTextCatalog> _catalog;
    private readonly ILogger<QuestionShapeClassifier> _logger;

    public QuestionShapeClassifier(
        IOptionsMonitor<CopilotTextCatalog> catalog,
        ILogger<QuestionShapeClassifier> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public QuestionShape Classify(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return QuestionShape.Simple;
        var lower = question.ToLowerInvariant();
        // Single source of truth: CopilotTextCatalog.QuestionShapeComplexHints. The catalog
        // ships universal defaults but operators override entirely via copilot-text.json.
        // No code-side fallback — the catalog field IS the configuration.
        var hints = _catalog.CurrentValue.QuestionShapeComplexHints;
        if (hints is null || hints.Count == 0) return QuestionShape.Simple;

        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            if (lower.Contains(hint.ToLowerInvariant()))
            {
                _logger.LogDebug("[QuestionShapeClassifier] hint '{Hint}' -> ComplexAnalytics", hint);
                return QuestionShape.ComplexAnalytics;
            }
        }

        return QuestionShape.Simple;
    }
}
