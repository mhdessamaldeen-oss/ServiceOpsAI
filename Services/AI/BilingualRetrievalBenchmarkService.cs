using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models.AI;
using Microsoft.EntityFrameworkCore;

namespace ServiceOpsAI.Services.AI
{
    public class BilingualRetrievalBenchmarkService
    {
        private readonly IWebHostEnvironment _env;
        private readonly ApplicationDbContext _context;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly ILogger<BilingualRetrievalBenchmarkService> _logger;
        private const string DefaultRelativePath = "Benchmarks/bilingual_retrieval_benchmark.json";

        public BilingualRetrievalBenchmarkService(
            IWebHostEnvironment env,
            ApplicationDbContext context,
            ISemanticSearchService semanticSearchService,
            ILogger<BilingualRetrievalBenchmarkService> logger)
        {
            _env = env;
            _context = context;
            _semanticSearchService = semanticSearchService;
            _logger = logger;
        }

        public string GetDefaultPath()
        {
            return Path.Combine(_env.ContentRootPath, DefaultRelativePath);
        }

        public async Task<BilingualRetrievalBenchmark> LoadAsync(string? path = null, CancellationToken cancellationToken = default)
        {
            var resolvedPath = path ?? GetDefaultPath();

            if (!File.Exists(resolvedPath))
            {
                _logger.LogWarning("Bilingual retrieval benchmark file was not found at {Path}", resolvedPath);
                return new BilingualRetrievalBenchmark();
            }

            await using var stream = File.OpenRead(resolvedPath);
            var benchmark = await JsonSerializer.DeserializeAsync<BilingualRetrievalBenchmark>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                },
                cancellationToken);

            if (benchmark == null)
            {
                _logger.LogWarning("Failed to deserialize bilingual retrieval benchmark from {Path}", resolvedPath);
                return new BilingualRetrievalBenchmark();
            }

            benchmark.Cases = benchmark.Cases
                .Where(c => c.IsValid)
                .ToList();

            return benchmark;
        }

        public async Task<BilingualRetrievalBenchmarkRunResult> RunAsync(string? path = null, string? bucket = null, string? caseId = null, CancellationToken cancellationToken = default)
        {
            var benchmark = await LoadAsync(path, cancellationToken);
            var selectedCases = benchmark.Cases
                .Where(c => (string.IsNullOrWhiteSpace(bucket) || string.Equals(c.Bucket, bucket, StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrWhiteSpace(caseId) || string.Equals(c.Id, caseId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var existingIds = await LoadExistingTicketIdsAsync(selectedCases, cancellationToken);

            var results = new List<RetrievalBenchmarkCaseResult>();

            foreach (var testCase in selectedCases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Benchmark preserves legacy behavior: precision threshold applied (this is what
                // the test-case expected outputs were measured against). The benchmark's
                // testCase.IncludeAllStatuses flag inverts to restrictToTerminalStatuses (true means
                // "search all statuses" i.e. don't restrict).
                var restrictToTerminal = !testCase.IncludeAllStatuses;
                var matches = testCase.SourceTicketId.HasValue
                    ? await _semanticSearchService.GetRelatedTicketsAsync(
                        testCase.SourceTicketId.Value, testCase.Count, testCase.StatusIds,
                        restrictToTerminalStatuses: restrictToTerminal,
                        requirePrecisionThreshold: true)
                    : await _semanticSearchService.SearchSimilarTicketsByTextAsync(
                        testCase.QueryText, testCase.Count, testCase.StatusIds,
                        restrictToTerminalStatuses: restrictToTerminal,
                        requirePrecisionThreshold: true);

                var returnedTicketIds = matches.Select(m => m.Ticket.Id).ToList();
                var hasExpectation = testCase.ExpectedTicketIds.Count > 0;
                var missingExpectedTicketIds = testCase.ExpectedTicketIds
                    .Where(id => !existingIds.Contains(id))
                    .ToList();

                // Tier-1.2 — if EVERY expected ticket is missing from the corpus this
                // case is meaningless ground truth; mark it so the headline metric can
                // exclude it. Previously these silently inflated the corpus and were
                // never counted as failures.
                var allExpectedMissing = hasExpectation && missingExpectedTicketIds.Count == testCase.ExpectedTicketIds.Count;

                // First-hit rank (1-based); null when nothing matched. Drives MRR and recall@k.
                int? firstHitRank = null;
                for (var i = 0; i < returnedTicketIds.Count; i++)
                {
                    if (testCase.ExpectedTicketIds.Contains(returnedTicketIds[i]))
                    {
                        firstHitRank = i + 1;
                        break;
                    }
                }

                // NDCG@5 with binary relevance — sum 1/log2(rank+1) for each expected
                // ticket that lands in the top 5, divided by the ideal DCG.
                var ndcg = ComputeNdcgAtK(returnedTicketIds, testCase.ExpectedTicketIds, k: 5);

                var isHit = hasExpectation
                    ? firstHitRank.HasValue
                    : (bool?)null;

                results.Add(new RetrievalBenchmarkCaseResult
                {
                    Id = testCase.Id,
                    Bucket = testCase.Bucket,
                    QueryLanguage = testCase.QueryLanguage,
                    QueryText = testCase.QueryText,
                    Intent = testCase.Intent,
                    SourceTicketId = testCase.SourceTicketId,
                    IsSourceTicketMissing = testCase.SourceTicketId.HasValue && !existingIds.Contains(testCase.SourceTicketId.Value),
                    HasExpectation = hasExpectation,
                    IsHit = isHit,
                    ExpectedTicketIds = testCase.ExpectedTicketIds,
                    MissingExpectedTicketIds = missingExpectedTicketIds,
                    ReturnedTicketIds = returnedTicketIds,
                    FirstHitRank = firstHitRank,
                    ReciprocalRank = firstHitRank.HasValue ? 1.0 / firstHitRank.Value : 0.0,
                    NdcgAt5 = ndcg,
                    AllExpectedMissingFromCorpus = allExpectedMissing,
                    Matches = matches.Select(m => new RetrievalBenchmarkMatchResult
                    {
                        TicketId = m.Ticket.Id,
                        TicketNumber = m.Ticket.TicketNumber,
                        Title = m.Ticket.Title,
                        Score = Math.Round(m.Score * 100, 2)
                    }).ToList()
                });
            }

            // Only cases that have at least ONE expected ticket actually present in the
            // corpus count toward the headline metrics. Cases where every expected
            // ticket is gone get flagged but don't inflate the denominator.
            var scored = results.Where(r => r.HasExpectation && !r.AllExpectedMissingFromCorpus).ToList();

            var bucketResults = results
                .GroupBy(r => r.Bucket)
                .Select(g =>
                {
                    var bucketScored = g.Where(x => x.HasExpectation && !x.AllExpectedMissingFromCorpus).ToList();
                    return new RetrievalBenchmarkBucketResult
                    {
                        Bucket = g.Key,
                        TotalCases = g.Count(),
                        EvaluatedCases = g.Count(x => x.HasExpectation),
                        HitCases = g.Count(x => x.IsHit == true),
                        Recall1 = RankRecallAtK(bucketScored, 1),
                        Recall5 = RankRecallAtK(bucketScored, 5),
                        MeanReciprocalRank = bucketScored.Count == 0 ? 0 : bucketScored.Average(c => c.ReciprocalRank),
                        Ndcg5 = bucketScored.Count == 0 ? 0 : bucketScored.Average(c => c.NdcgAt5)
                    };
                })
                .OrderBy(r => r.Bucket)
                .ToList();

            var runResult = new BilingualRetrievalBenchmarkRunResult
            {
                Version = benchmark.Version,
                RunOnUtc = DateTime.UtcNow,
                TotalCases = results.Count,
                EvaluatedCases = results.Count(r => r.HasExpectation),
                HitCases = results.Count(r => r.IsHit == true),
                Recall1 = RankRecallAtK(scored, 1),
                Recall3 = RankRecallAtK(scored, 3),
                Recall5 = RankRecallAtK(scored, 5),
                Recall10 = RankRecallAtK(scored, 10),
                MeanReciprocalRank = scored.Count == 0 ? 0 : scored.Average(c => c.ReciprocalRank),
                Ndcg5 = scored.Count == 0 ? 0 : scored.Average(c => c.NdcgAt5),
                Buckets = bucketResults,
                Cases = results
            };

            // Capture history for full or bucket runs (skip for single specific cases to avoid noise)
            if (string.IsNullOrEmpty(caseId))
            {
                try
                {
                    var settings = await _semanticSearchService.GetTuningSettingsAsync();
                    var run = new RetrievalBenchmarkRun
                {
                    RunOnUtc = runResult.RunOnUtc,
                    TotalCases = runResult.TotalCases,
                    EvaluatedCases = runResult.EvaluatedCases,
                    HitCases = runResult.HitCases,
                    HitRate = runResult.EvaluatedCases > 0 ? (double)runResult.HitCases / runResult.EvaluatedCases : 0,
                    // Tier-1.3 — persisted as columns so SQL can chart deltas across changes.
                    Recall1 = runResult.Recall1,
                    Recall3 = runResult.Recall3,
                    Recall5 = runResult.Recall5,
                    Recall10 = runResult.Recall10,
                    MeanReciprocalRank = runResult.MeanReciprocalRank,
                    Ndcg5 = runResult.Ndcg5,
                    Version = runResult.Version,
                    SettingsJson = JsonSerializer.Serialize(settings),
                    ResultsJson = JsonSerializer.Serialize(results.Select(r => new {
                        r.Id,
                        r.IsHit,
                        r.FirstHitRank,
                        r.ReciprocalRank,
                        r.NdcgAt5,
                        r.AllExpectedMissingFromCorpus,
                        Score = r.Matches.Any() ? r.Matches[0].Score : 0
                    }))
                };
                _context.RetrievalBenchmarkRuns.Add(run);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save benchmark run history");
            }
            } // Close if (string.IsNullOrEmpty(caseId))

            return runResult;
        }

        public async Task<RetrievalBenchmarkValidationResult> ValidateAsync(string? path = null, string? bucket = null, CancellationToken cancellationToken = default)
        {
            var benchmark = await LoadAsync(path, cancellationToken);
            var selectedCases = benchmark.Cases
                .Where(c => string.IsNullOrWhiteSpace(bucket) || string.Equals(c.Bucket, bucket, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var existingIds = await LoadExistingTicketIdsAsync(selectedCases, cancellationToken);

            var cases = selectedCases.Select(testCase =>
            {
                var missingExpected = testCase.ExpectedTicketIds
                    .Where(id => !existingIds.Contains(id))
                    .ToList();
                var sourceMissing = testCase.SourceTicketId.HasValue && !existingIds.Contains(testCase.SourceTicketId.Value);
                var warnings = new List<string>();

                if (sourceMissing && testCase.SourceTicketId.HasValue)
                {
                    warnings.Add($"Missing source ticket id: {testCase.SourceTicketId.Value}");
                }

                if (missingExpected.Count > 0)
                {
                    warnings.Add($"Missing expected ticket ids: {string.Join(", ", missingExpected)}");
                }

                return new RetrievalBenchmarkValidationCase
                {
                    Id = testCase.Id,
                    Bucket = testCase.Bucket,
                    SourceTicketId = testCase.SourceTicketId,
                    IsSourceTicketMissing = sourceMissing,
                    ExpectedTicketIds = testCase.ExpectedTicketIds,
                    MissingExpectedTicketIds = missingExpected,
                    Warnings = warnings
                };
            }).ToList();

            return new RetrievalBenchmarkValidationResult
            {
                Version = benchmark.Version,
                ValidatedOnUtc = DateTime.UtcNow,
                TotalCases = cases.Count,
                CasesWithWarnings = cases.Count(c => c.Warnings.Count > 0),
                Cases = cases
            };
        }

        private async Task<HashSet<int>> LoadExistingTicketIdsAsync(IEnumerable<RetrievalBenchmarkCase> cases, CancellationToken cancellationToken)
        {
            var relevantIds = cases
                .SelectMany(c => c.ExpectedTicketIds.Concat(c.SourceTicketId.HasValue ? new[] { c.SourceTicketId.Value } : Array.Empty<int>()))
                .Distinct()
                .ToList();

            if (relevantIds.Count == 0)
            {
                return new HashSet<int>();
            }

            var ids = await _context.Tickets
                .AsNoTracking()
                .Where(t => relevantIds.Contains(t.Id) && !t.IsDeleted)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            return ids.ToHashSet();
        }

        // ── Tier-1 helpers — rank-aware metrics ─────────────────────────────
        // recall@k = (cases where ≥1 expected ticket appeared in top-k) / (cases).
        // We use first-hit rank (already computed per-case) to answer this in O(1).
        private static double RankRecallAtK(IReadOnlyList<RetrievalBenchmarkCaseResult> cases, int k)
        {
            if (cases.Count == 0) return 0;
            var hits = cases.Count(c => c.FirstHitRank.HasValue && c.FirstHitRank.Value <= k);
            return (double)hits / cases.Count;
        }

        // Binary-relevance NDCG@k: every expected ticket gets relevance 1, others 0.
        // Sufficient for our ground-truth shape (boolean "expected" lists) — we don't
        // store graded relevance in the benchmark file.
        private static double ComputeNdcgAtK(IReadOnlyList<int> returned, IReadOnlyList<int> expected, int k)
        {
            if (returned.Count == 0 || expected.Count == 0) return 0;
            double dcg = 0;
            var take = Math.Min(k, returned.Count);
            for (var i = 0; i < take; i++)
            {
                if (expected.Contains(returned[i]))
                {
                    // i is 0-based; classical DCG uses log2(rank + 1) with 1-based rank,
                    // so the denominator becomes log2((i + 1) + 1) = log2(i + 2).
                    dcg += 1.0 / Math.Log2(i + 2);
                }
            }
            // Ideal DCG: as many relevant docs as possible at the top.
            var idealHits = Math.Min(expected.Count, k);
            double idcg = 0;
            for (var i = 0; i < idealHits; i++)
            {
                idcg += 1.0 / Math.Log2(i + 2);
            }
            return idcg <= 0 ? 0 : dcg / idcg;
        }
    }
}
