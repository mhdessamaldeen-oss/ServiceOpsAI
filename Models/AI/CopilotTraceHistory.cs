using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Models.AI
{
    // ── Index strategy ─────────────────────────────────────────────────────────────
    // SourceSuite + CreatedAt: assessment grid + suite reports filter by suite name and
    // sort by recency. Without this the table scans on every assessment-lab page load.
    // EstimatedCostUsd + CreatedAt: cost-over-time dashboards order by cost or filter by
    // window. EstimatedCostUsd alone would seek; pairing with CreatedAt lets one composite
    // index cover both "top expensive last week" and "spend by day" queries.
    // CaseCode is already implicitly indexed via the existing grid-query path — leaving alone.
    // Additional indexes added 2026-05-29 to back the investigation page lookups:
    //  • CaseCode + CreatedAt: assessment grid "open this case" navigation (was table-scanning).
    //  • PipelineTraceId: deep-link / X-correlation lookups (one row per pipeline run).
    //  • SessionId + CreatedAt: chat-history-by-session retrieval (the chat sidebar).
    [Index(nameof(SourceSuite), nameof(CreatedAt), Name = "IX_CopilotTraceHistory_SourceSuite_CreatedAt")]
    [Index(nameof(EstimatedCostUsd), nameof(CreatedAt), Name = "IX_CopilotTraceHistory_Cost_CreatedAt")]
    [Index(nameof(CaseCode), nameof(CreatedAt), Name = "IX_CopilotTraceHistory_CaseCode_CreatedAt")]
    [Index(nameof(PipelineTraceId), Name = "IX_CopilotTraceHistory_PipelineTraceId")]
    [Index(nameof(SessionId), nameof(CreatedAt), Name = "IX_CopilotTraceHistory_Session_CreatedAt")]
    public class CopilotTraceHistory
    {
        public int Id { get; set; }

        [Required]
        public string Question { get; set; } = string.Empty;

        public string? Answer { get; set; }

        public string ExecutionPlan { get; set; } = "{}";
        public string ExecutionTimes { get; set; } = "{}";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string? ModelName { get; set; }

        /// <summary>
        /// Per-pipeline-step model usage. JSON object keyed by stage name (e.g.
        /// <c>{"IntentClassifier":"qwen2.5-coder:7b","SpecExtractor":"qwen2.5-coder:7b","Decomposer":"gpt-4o-mini","Compiler":null,"Executor":null,"Explainer":"qwen2.5-coder:7b"}</c>).
        /// Lets the trace grid show "which model did what" without parsing ExecutionPlan.
        /// Null for legacy rows + for traces with no LLM call data.
        /// </summary>
        public string? StepModelsJson { get; set; }

        public long TotalElapsedMs { get; set; }
        
        public int? SessionId { get; set; }
        public virtual CopilotChatSession? Session { get; set; }
        
        [StringLength(64)]
        public string? CaseCode { get; set; }

        /// <summary>
        /// The assessment SUITE FILE this question came from (e.g. "10-aggregation-count-sum-avg-2026-05-08.json").
        /// Populated only for runs triggered from the Assessment Lab UI; NULL for chat-copilot
        /// questions. Lets the developer / triage agent answer "which file produced this trace?"
        /// without inferring from CaseCode prefix conventions.
        /// </summary>
        [StringLength(200)]
        public string? SourceSuite { get; set; }

        [StringLength(128)]
        public string? PipelineTraceId { get; set; }

        [StringLength(maximumLength: 4000)]
        public string? GeneratedScript { get; set; }

        /// <summary>
        /// Curated reference SQL the question was authored against (the assessment case's GoldSql).
        /// Stored alongside <see cref="GeneratedScript"/> so a single trace row carries both the
        /// expected query and what the pipeline actually produced — no join across tables, no
        /// re-reading the suite JSON to diff them. NULL for chat-copilot traces (no expected
        /// answer) and for assessment cases whose authors didn't supply GoldSql.
        /// </summary>
        [StringLength(maximumLength: 4000)]
        public string? ExpectedScript { get; set; }

        /// <summary>
        /// First/most-important error captured during this question's processing pipeline. Populated when:
        ///   - the classifier LLM call failed (HTTP error, MAX_TOKENS, SAFETY block, parse failure)
        ///   - any executor step finished with Status=Error
        ///   - the planner/composer returned null SQL
        /// Null when the question completed without any error path being hit. Surfaced next to
        /// GeneratedScript so it's easy to scan in the trace grid without parsing ExecutionPlan JSON.
        /// </summary>
        [StringLength(maximumLength: 2000)]
        public string? ErrorMessage { get; set; }

        // ── LLM cost / usage roll-up ────────────────────────────────────────────────
        // These four columns aggregate per-call telemetry from every LLM invocation that
        // ran during this question (decomposer, spec extractor, intent classifier, tool
        // handler, explainer — anywhere ILlmClient is called). Per-call breakdowns live
        // inside ExecutionPlan.Steps[].Metrics; these columns exist so the trace grid
        // and per-question cost queries don't have to parse JSON for every row.

        /// <summary>Total number of LLM calls fired while answering this question. Helps
        /// spot pathological retry loops (a question that triggered 8 LLM calls is suspicious)
        /// without parsing the step list.</summary>
        public int? LlmCallCount { get; set; }

        /// <summary>Sum of <c>PromptTokens</c> across every LLM call recorded for this trace.</summary>
        public int? TotalPromptTokens { get; set; }

        /// <summary>Sum of <c>CompletionTokens</c> across every LLM call recorded for this trace.</summary>
        public int? TotalCompletionTokens { get; set; }

        /// <summary>USD cost estimate for this question, summed from per-call costs computed
        /// from the <see cref="ModelPricing"/> rates active when the call ran. <c>decimal(12,6)</c>
        /// holds rates down to one millionth of a dollar — enough resolution for $0.000075/1K
        /// rates without rounding.</summary>
        [Column(TypeName = "decimal(12,6)")]
        public decimal? EstimatedCostUsd { get; set; }

        /// <summary>
        /// Embedding vector of <see cref="Question"/>, JSON-serialized float array. Populated only
        /// for SUCCESSFUL traces (no error, valid SQL produced). Used by the few-shot retriever
        /// (<c>IPastQuestionRetriever</c>) to find similar past questions and inject their plans
        /// into the classifier prompt as worked examples.
        ///
        /// <para>Stored as JSON string (matches the convention in <c>TicketSemanticEmbedding</c>);
        /// SQL Server has no native vector type. Use the <see cref="QuestionEmbedding"/> NotMapped
        /// helper to read/write as <c>float[]</c>.</para>
        /// </summary>
        public string? QuestionEmbeddingJson { get; set; }

        /// <summary>The embedder model that generated <see cref="QuestionEmbeddingJson"/> — vectors
        /// from different models aren't comparable, so the retriever must filter by this.</summary>
        [StringLength(100)]
        public string? EmbeddingModelName { get; set; }

        /// <summary>NotMapped helper: read/write the embedding as a <c>float[]</c>. Returns null
        /// when the underlying JSON is null/empty.</summary>
        [NotMapped]
        public float[]? QuestionEmbedding
        {
            get => string.IsNullOrEmpty(QuestionEmbeddingJson)
                ? null
                : JsonSerializer.Deserialize<float[]>(QuestionEmbeddingJson);
            set => QuestionEmbeddingJson = value == null ? null : JsonSerializer.Serialize(value);
        }
    }
}
