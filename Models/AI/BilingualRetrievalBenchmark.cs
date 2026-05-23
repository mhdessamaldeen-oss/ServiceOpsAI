using System.Text.Json.Serialization;

namespace ServiceOpsAI.Models.AI
{
    public class BilingualRetrievalBenchmark
    {
        public string Version { get; set; } = "1.0";
        public DateTime CreatedOnUtc { get; set; } = DateTime.UtcNow;
        public List<RetrievalBenchmarkCase> Cases { get; set; } = new();
    }

    public class RetrievalBenchmarkCase
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public string QueryLanguage { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public int Count { get; set; } = 5;
        public bool IncludeAllStatuses { get; set; }
        public List<int> StatusIds { get; set; } = new();
        public List<int> ExpectedTicketIds { get; set; } = new();
        public string Intent { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Id) &&
            !string.IsNullOrWhiteSpace(Bucket) &&
            !string.IsNullOrWhiteSpace(QueryText);
    }

    public class BilingualRetrievalBenchmarkRunResult
    {
        public string Version { get; set; } = "1.0";
        public DateTime RunOnUtc { get; set; } = DateTime.UtcNow;
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public List<RetrievalBenchmarkBucketResult> Buckets { get; set; } = new();
        public List<RetrievalBenchmarkCaseResult> Cases { get; set; } = new();

        // ── Tier-1 rank-aware metrics ──────────────────────────────────────
        // Hit-rate alone reports "expected ticket appeared anywhere in top-K"
        // and treats rank-1 and rank-8 the same. The metrics below describe
        // ranking quality so changes to the RAG pipeline get a measurable delta.
        public double Recall1 { get; set; }
        public double Recall3 { get; set; }
        public double Recall5 { get; set; }
        public double Recall10 { get; set; }
        public double MeanReciprocalRank { get; set; }
        public double Ndcg5 { get; set; }
    }

    public class RetrievalBenchmarkBucketResult
    {
        public string Bucket { get; set; } = string.Empty;
        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public double Recall1 { get; set; }
        public double Recall5 { get; set; }
        public double MeanReciprocalRank { get; set; }
        public double Ndcg5 { get; set; }
    }

    public class RetrievalBenchmarkCaseResult
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public string QueryLanguage { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public string Intent { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public bool IsSourceTicketMissing { get; set; }
        public bool HasExpectation { get; set; }
        public bool? IsHit { get; set; }
        public List<int> ExpectedTicketIds { get; set; } = new();
        public List<int> MissingExpectedTicketIds { get; set; } = new();
        public List<int> ReturnedTicketIds { get; set; } = new();
        public List<RetrievalBenchmarkMatchResult> Matches { get; set; } = new();

        // Tier-1 per-case rank metrics. FirstHitRank is 1-based; null when no expected
        // ticket appeared in the returned list. ReciprocalRank = 1/FirstHitRank (0 on miss).
        public int? FirstHitRank { get; set; }
        public double ReciprocalRank { get; set; }
        public double NdcgAt5 { get; set; }

        // Tier-1.2 — distinguishes "model failed to find it" from "corpus didn't have it"
        // so the headline metric isn't quietly inflated by ground-truth tickets that were
        // deleted from the index.
        public bool AllExpectedMissingFromCorpus { get; set; }
    }

    public class RetrievalBenchmarkMatchResult
    {
        public int TicketId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public double Score { get; set; }
    }

    public class RetrievalBenchmarkValidationResult
    {
        public string Version { get; set; } = "1.0";
        public DateTime ValidatedOnUtc { get; set; } = DateTime.UtcNow;
        public int TotalCases { get; set; }
        public int CasesWithWarnings { get; set; }
        public List<RetrievalBenchmarkValidationCase> Cases { get; set; } = new();
    }

    public class RetrievalBenchmarkValidationCase
    {
        public string Id { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public int? SourceTicketId { get; set; }
        public bool IsSourceTicketMissing { get; set; }
        public List<int> ExpectedTicketIds { get; set; } = new();
        public List<int> MissingExpectedTicketIds { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
