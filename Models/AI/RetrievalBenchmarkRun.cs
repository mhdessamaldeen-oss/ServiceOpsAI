using System;
using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models.AI
{
    public class RetrievalBenchmarkRun
    {
        [Key]
        public int Id { get; set; }
        public DateTime RunOnUtc { get; set; }

        public int TotalCases { get; set; }
        public int EvaluatedCases { get; set; }
        public int HitCases { get; set; }
        public double HitRate { get; set; }

        // Tier-1.3 — rank-aware metrics persisted as their own columns (not buried in
        // ResultsJson) so SQL can plot trends across A/B changes to the RAG pipeline.
        public double Recall1 { get; set; }
        public double Recall3 { get; set; }
        public double Recall5 { get; set; }
        public double Recall10 { get; set; }
        public double MeanReciprocalRank { get; set; }
        public double Ndcg5 { get; set; }

        public string Version { get; set; } = string.Empty;

        // Stores the configuration used (Weights/Toggles) as JSON
        public string SettingsJson { get; set; } = string.Empty;

        // Stores the individual case results (id, isHit) as JSON
        public string ResultsJson { get; set; } = string.Empty;
    }
}
