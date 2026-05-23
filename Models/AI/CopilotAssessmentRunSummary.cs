using System.ComponentModel.DataAnnotations;

namespace ServiceOpsAI.Models.AI
{
    /// <summary>
    /// One row per assessment run. Lets us compare pass-rate / latency across model versions and planner changes.
    /// </summary>
    public class CopilotAssessmentRunSummary
    {
        public int Id { get; set; }

        public Guid RunId { get; set; }

        public DateTime RunAt { get; set; } = DateTime.UtcNow;

        [StringLength(128)]
        public string? ModelName { get; set; }

        public int TotalCases { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }

        public long AvgLatencyMs { get; set; }
        public long MaxLatencyMs { get; set; }

        /// <summary>JSON array of failed case codes (e.g. ["SEL-002","FILT-008"]).</summary>
        public string FailedCaseCodes { get; set; } = "[]";

        /// <summary>Optional free-text label so users can tag a run (e.g. "post-cleanup", "baseline").</summary>
        [StringLength(256)]
        public string? Label { get; set; }

        public double SuccessRate => TotalCases == 0 ? 0 : (double)PassCount / TotalCases;
    }
}
