using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using System.Text.Json;

namespace ServiceOpsAI.Services.AI.Retrieval
{
    public sealed class RecommendationGroundingAuditor : IRecommendationGroundingAuditor
    {
        private readonly IAiProviderFactory _providerFactory;
        private readonly ILogger<RecommendationGroundingAuditor> _logger;

        public RecommendationGroundingAuditor(
            IAiProviderFactory providerFactory,
            ILogger<RecommendationGroundingAuditor> logger)
        {
            _providerFactory = providerFactory;
            _logger = logger;
        }

        public async Task<RecommendationGroundingAudit> AuditAsync(
            string summary,
            string recommendedAction,
            IReadOnlyList<string> evidenceChunks,
            CancellationToken cancellationToken = default)
        {
            // No evidence to audit against → can't be grounded, return Weak signal.
            if (evidenceChunks == null || evidenceChunks.Count == 0)
            {
                return new RecommendationGroundingAudit
                {
                    Confidence = 0.0,
                    WasAudited = false,
                    Notes = "No retrieved evidence; grounding cannot be verified."
                };
            }

            if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(recommendedAction))
            {
                return new RecommendationGroundingAudit { Confidence = 0.0, WasAudited = false };
            }

            try
            {
                // The grounding audit uses the same provider workload as RAG itself —
                // a small, cheap completion. We're asking the model to be a judge,
                // not a generator, so we want the cheapest tier available.
                var provider = _providerFactory.GetProviderForWorkload(AiWorkloadType.Rag);
                var prompt = BuildAuditPrompt(summary, recommendedAction, evidenceChunks);
                var result = await provider.GenerateAsync(prompt);

                if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    // Fall back to text-overlap heuristic when the audit LLM call fails;
                    // better a noisy signal than no signal.
                    return HeuristicAudit(summary, recommendedAction, evidenceChunks,
                        "LLM audit unavailable; used text-overlap heuristic.");
                }

                return ParseAuditResponse(result.ResponseText) ?? HeuristicAudit(
                    summary, recommendedAction, evidenceChunks,
                    "LLM audit response unparseable; used text-overlap heuristic.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recommendation grounding audit failed; falling back to heuristic.");
                return HeuristicAudit(summary, recommendedAction, evidenceChunks,
                    "Audit exception; used text-overlap heuristic. " + ex.Message);
            }
        }

        private static string BuildAuditPrompt(string summary, string recommendedAction, IReadOnlyList<string> evidence)
        {
            var labeledEvidence = evidence.Select((e, i) => $"[E{i + 1}] {e}");
            var evidenceBlock = string.Join("\n---\n", labeledEvidence);

            return $@"You are a grounding auditor. Your job is to detect hallucinations.

Below is an AI-generated support recommendation and the evidence chunks that were retrieved before generating it. Decide whether each material claim in the recommendation is supported by the evidence.

A claim is SUPPORTED if a reasonable reader, looking at the evidence, would agree the recommendation accurately reflects it. A claim is UNSUPPORTED if it goes beyond the evidence, introduces facts not present, or contradicts the evidence.

Respond ONLY in this JSON format (no surrounding text):
{{
  ""confidence"": <number 0.0-1.0, fraction of material claims that are supported>,
  ""unsupportedClaims"": [<short string per unsupported claim, max 5>],
  ""notes"": ""<one-sentence rationale>""
}}

EVIDENCE:
{evidenceBlock}

RECOMMENDATION SUMMARY:
{summary}

RECOMMENDED ACTION:
{recommendedAction}";
        }

        private static RecommendationGroundingAudit? ParseAuditResponse(string responseText)
        {
            try
            {
                var trimmed = responseText.Trim();
                var startIdx = trimmed.IndexOf('{');
                var endIdx = trimmed.LastIndexOf('}');
                if (startIdx < 0 || endIdx <= startIdx) return null;

                var json = trimmed[startIdx..(endIdx + 1)];
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(c.GetDouble(), 0.0, 1.0)
                    : 1.0;

                var unsupported = new List<string>();
                if (root.TryGetProperty("unsupportedClaims", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) unsupported.Add(s);
                    }
                }

                var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";

                return new RecommendationGroundingAudit
                {
                    Confidence = confidence,
                    UnsupportedClaims = unsupported,
                    WasAudited = true,
                    Notes = notes
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Last-resort grounding check when the LLM audit can't run. Tokenizes both
        /// the recommendation and the evidence and reports the fraction of distinct
        /// recommendation tokens that appear somewhere in the evidence. Crude — but
        /// reliable enough to flag truly egregious hallucinations.
        /// </summary>
        private static RecommendationGroundingAudit HeuristicAudit(
            string summary,
            string recommendedAction,
            IReadOnlyList<string> evidenceChunks,
            string notes)
        {
            var recommendationText = ($"{summary}\n{recommendedAction}").ToLowerInvariant();
            var evidenceText = string.Join("\n", evidenceChunks).ToLowerInvariant();

            var recTokens = Tokenize(recommendationText);
            var evTokens = Tokenize(evidenceText);

            if (recTokens.Count == 0)
            {
                return new RecommendationGroundingAudit { Confidence = 0.0, WasAudited = false, Notes = notes };
            }

            var overlap = recTokens.Count(t => evTokens.Contains(t));
            var confidence = (double)overlap / recTokens.Count;

            return new RecommendationGroundingAudit
            {
                Confidence = confidence,
                WasAudited = true,
                Notes = notes
            };
        }

        private static HashSet<string> Tokenize(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in text.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\n', '\t', '(', ')', '[', ']', '"', '\'' },
                                            StringSplitOptions.RemoveEmptyEntries))
            {
                // Heuristic stop-token filter; the goal is to catch claims that
                // mention specific nouns/IDs not present in the evidence, so we
                // strip very short tokens that contribute no semantic signal.
                if (token.Length >= 4)
                {
                    set.Add(token);
                }
            }
            return set;
        }
    }
}
