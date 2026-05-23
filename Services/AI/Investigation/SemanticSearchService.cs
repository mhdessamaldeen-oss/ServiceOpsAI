using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Constants;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Services.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Services.AI.Copilot.Diagnostics;
using ServiceOpsAI.Services.AI.Common;
using System.Text;

namespace ServiceOpsAI.Services.AI
{
    public class SemanticSearchService : ISemanticSearchService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ITicketEmbedder _embeddingEngine;
        private readonly TicketContextPreparationService _contextPreparationService;
        private readonly ILocalizationService _localizer;
        private readonly ILogger<SemanticSearchService> _logger;


        public SemanticSearchService(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            ITicketEmbedder embeddingEngine,
            TicketContextPreparationService contextPreparationService,
            ILocalizationService localizer,
            ILogger<SemanticSearchService> logger)
        {
            _contextFactory = contextFactory;
            _embeddingEngine = embeddingEngine;
            _contextPreparationService = contextPreparationService;
            _localizer = localizer;
            _logger = logger;
        }

        public async Task UpsertTicketEmbeddingAsync(int ticketId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var ticketExists = await context.Tickets.AnyAsync(t => t.Id == ticketId);
            if (!ticketExists)
            {
                _logger.LogWarning("Attempted to generate embedding for non-existent ticket {TicketId}", ticketId);
                return;
            }

            var settings = await GetTuningSettingsAsync();
            var ticketContext = await _contextPreparationService.PrepareAsync(ticketId);
            var textToEmbed = NormalizeEmbeddingText(BuildEmbeddingText(ticketContext, settings));

            _logger.LogInformation("Generating local embedding for ticket {TicketId} using model '{Model}'",
                ticketId, _embeddingEngine.ModelName);

            var vector = _embeddingEngine.GenerateEmbedding(textToEmbed);
            if (vector == null || vector.Length == 0)
            {
                throw new InvalidOperationException("Local embedding engine returned an empty vector.");
            }

            _logger.LogInformation("Successfully generated embedding ({Size} dims) for ticket {TicketId}", vector.Length, ticketId);

            var existing = await context.TicketSemanticEmbeddings.FindAsync(ticketId);
            if (existing != null)
            {
                existing.Vector = vector;
                existing.ModelName = _embeddingEngine.ModelName;
                existing.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                context.TicketSemanticEmbeddings.Add(new TicketSemanticEmbedding
                {
                    TicketId = ticketId,
                    Vector = vector,
                    ModelName = _embeddingEngine.ModelName
                });
            }

            await context.SaveChangesAsync();
        }

        public async Task<List<SemanticSearchMatch>> GetRelatedTicketsAsync(
            int ticketId,
            int count = 5,
            List<int>? statusIds = null,
            bool restrictToTerminalStatuses = false,
            bool requirePrecisionThreshold = false,
            CopilotTraceSink? trace = null,
            CancellationToken cancellationToken = default)
        {
            using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            try
            {
                var settings = await GetTuningSettingsAsync();
                var seedSw = Stopwatch.StartNew();
                var targetTicket = await context.Tickets
                    .Include(t => t.Status)
                    .Include(t => t.Category)
                    .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

                if (targetTicket == null)
                {
                    _logger.LogWarning("Target ticket {TicketId} was not found for semantic search.", ticketId);
                    trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                        "ResolveSeedTicket", "Lookup seed ticket by Id.",
                        input: ticketId.ToString(), output: "not found", elapsedMs: seedSw.ElapsedMilliseconds,
                        status: CopilotStepStatus.Warn);
                    return new List<SemanticSearchMatch>();
                }

                var targetContext = await _contextPreparationService.PrepareAsync(ticketId, cancellationToken);
                var targetQueryText = NormalizeEmbeddingText(BuildEmbeddingText(targetContext, settings));
                var targetProfile = TicketLanguageDetector.DetectProfile(targetQueryText);

                var targetEmbedding = await context.TicketSemanticEmbeddings.FindAsync(new object[] { ticketId }, cancellationToken);
                if (targetEmbedding == null)
                {
                    _logger.LogInformation("Embedding missing for ticket {TicketId}, triggering on-the-fly generation...", ticketId);
                    await UpsertTicketEmbeddingAsync(ticketId);
                    targetEmbedding = await context.TicketSemanticEmbeddings.FindAsync(new object[] { ticketId }, cancellationToken);
                }
                seedSw.Stop();

                if (targetEmbedding == null || targetEmbedding.Vector.Length == 0)
                {
                    var msg = $"Seed ticket {ticketId} has no embedding (or embedding is empty). " +
                              $"This usually means the embedder ('{_embeddingEngine.ModelName}') failed when the ticket was created. " +
                              $"Verify Ollama is up and the model is pulled (ollama pull {_embeddingEngine.ModelName}), then re-trigger ticket embedding.";
                    _logger.LogWarning(msg);
                    trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                        "LoadSeedEmbedding", "Load the stored vector for the seed ticket.",
                        input: ticketId.ToString(), output: "missing/empty", elapsedMs: seedSw.ElapsedMilliseconds,
                        status: CopilotStepStatus.Error);
                    throw new InvalidOperationException(msg);
                }

                var targetVector = targetEmbedding.Vector;
                var targetModel = targetEmbedding.ModelName;
                trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                    "LoadSeedEmbedding", "Load the stored vector for the seed ticket.",
                    input: $"ticketId={ticketId}",
                    output: $"vector dim={targetVector.Length}, model={targetModel}",
                    elapsedMs: seedSw.ElapsedMilliseconds);

                var candSw = Stopwatch.StartNew();
                var candidates = await BuildCandidateQuery(context, targetModel, statusIds, !restrictToTerminalStatuses, ticketId).ToListAsync(cancellationToken);
                candSw.Stop();

                _logger.LogInformation("Found {Count} candidate tickets for semantic matching with ticket {TicketId}", candidates.Count, ticketId);
                trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                    "BuildCandidateQuery", "Load candidate vectors from TicketSemanticEmbeddings.",
                    input: $"model={targetModel}, restrictToTerminalStatuses={restrictToTerminalStatuses}, excludeId={ticketId}",
                    output: $"candidates={candidates.Count}",
                    elapsedMs: candSw.ElapsedMilliseconds);

                if (candidates.Count == 0)
                {
                    _logger.LogWarning("No embedding candidates found for ticket {TicketId} under model '{Model}'.", ticketId, targetModel);
                    return new List<SemanticSearchMatch>();
                }

                var scoreSw = Stopwatch.StartNew();
                var scored = ScoreCandidates(candidates, targetVector, targetQueryText, targetProfile, count, settings, !requirePrecisionThreshold);
                scoreSw.Stop();

                _logger.LogInformation("Semantic search for ticket {TicketId} returned {ResultCount} matches.", ticketId, scored.Count);
                trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                    "ScoreCandidates", "Compute hybrid score (vector + lexical + language boost) per candidate.",
                    input: $"candidates={candidates.Count}, threshold={(requirePrecisionThreshold ? "applied" : "bypassed")}",
                    output: $"matches={scored.Count}, topScores=[{string.Join(", ", scored.Take(5).Select(m => m.Score.ToString("F2")))}]",
                    elapsedMs: scoreSw.ElapsedMilliseconds);

                return scored;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Semantic search error for ticket {TicketId}", ticketId);
                return new List<SemanticSearchMatch>();
            }
        }

        public async Task<List<SemanticSearchMatch>> SearchSimilarTicketsByTextAsync(
            string queryText,
            int count = 5,
            List<int>? statusIds = null,
            bool restrictToTerminalStatuses = false,
            bool requirePrecisionThreshold = false,
            CopilotTraceSink? trace = null,
            CancellationToken cancellationToken = default)
        {
            using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            try
            {
                if (string.IsNullOrWhiteSpace(queryText))
                {
                    return new List<SemanticSearchMatch>();
                }

                var settings = await GetTuningSettingsAsync();
                var normalizedText = NormalizeEmbeddingText(queryText);
                var queryLanguage = TicketLanguageDetector.DetectProfile(normalizedText);

                var embedSw = Stopwatch.StartNew();
                var targetVector = _embeddingEngine.GenerateEmbedding(normalizedText);
                embedSw.Stop();

                if (targetVector == null || targetVector.Length == 0)
                {
                    // Throwing instead of swallowing: a silent "0 matches" gives the user no clue
                    // that the embedder is broken (Ollama down, model not pulled, network issue).
                    // Callers wrap this in try/catch and surface the message to the trace step.
                    var msg = $"Embedder '{_embeddingEngine.ModelName}' returned an empty vector. " +
                              $"Likely cause: the model isn't pulled in Ollama. Run: ollama pull {_embeddingEngine.ModelName}";
                    _logger.LogWarning(msg);
                    trace?.RecordLlmCall(CopilotExecutionLayer.DataExecution, "Embedder",
                        providerName: "Embedder", modelName: _embeddingEngine.ModelName,
                        prompt: normalizedText, responseText: "(empty vector)",
                        elapsedMs: embedSw.ElapsedMilliseconds, providerSuccess: false,
                        providerError: "Empty vector returned (model likely not available)");
                    throw new InvalidOperationException(msg);
                }

                trace?.RecordLlmCall(CopilotExecutionLayer.DataExecution, "Embedder",
                    providerName: "Embedder", modelName: _embeddingEngine.ModelName,
                    prompt: normalizedText, responseText: $"vector(dim={targetVector.Length})",
                    elapsedMs: embedSw.ElapsedMilliseconds, providerSuccess: true);

                var candSw = Stopwatch.StartNew();
                var candidates = await BuildCandidateQuery(context, _embeddingEngine.ModelName, statusIds, !restrictToTerminalStatuses).ToListAsync(cancellationToken);
                candSw.Stop();

                trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                    "BuildCandidateQuery", "Load candidate vectors from TicketSemanticEmbeddings.",
                    input: $"model={_embeddingEngine.ModelName}, restrictToTerminalStatuses={restrictToTerminalStatuses}",
                    output: $"candidates={candidates.Count}",
                    elapsedMs: candSw.ElapsedMilliseconds);

                if (candidates.Count == 0)
                {
                    _logger.LogWarning("No semantic-search candidates found for model '{Model}' (table TicketSemanticEmbeddings is empty for this model).", _embeddingEngine.ModelName);
                    return new List<SemanticSearchMatch>();
                }

                var scoreSw = Stopwatch.StartNew();
                var scored = ScoreCandidates(candidates, targetVector, normalizedText, queryLanguage, count, settings, !requirePrecisionThreshold);
                scoreSw.Stop();

                trace?.RecordFunctionCall(CopilotExecutionLayer.DataExecution,
                    "ScoreCandidates", "Compute hybrid score (vector + lexical + language boost) per candidate.",
                    input: $"candidates={candidates.Count}, threshold={(requirePrecisionThreshold ? "applied" : "bypassed")}",
                    output: $"matches={scored.Count}, topScores=[{string.Join(", ", scored.Take(5).Select(m => m.Score.ToString("F2")))}]",
                    elapsedMs: scoreSw.ElapsedMilliseconds);

                return scored;
            }
            catch (InvalidOperationException)
            {
                // Re-throw so callers see the actionable message instead of getting empty results.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Free-text semantic search failed.");
                throw new InvalidOperationException($"Semantic search failed: {ex.GetBaseException().Message}", ex);
            }
        }

        public async Task<RetrievalTuningSettings> GetTuningSettingsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var setting = await context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "RetrievalTuningSettings");
            if (setting == null || string.IsNullOrWhiteSpace(setting.Value))
            {
                return new RetrievalTuningSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<RetrievalTuningSettings>(setting.Value) ?? new RetrievalTuningSettings();
            }
            catch
            {
                return new RetrievalTuningSettings();
            }
        }

        public async Task UpdateTuningSettingsAsync(RetrievalTuningSettings settings)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var setting = await context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "RetrievalTuningSettings");
            var json = JsonSerializer.Serialize(settings);

            if (setting == null)
            {
                context.SystemSettings.Add(new SystemSetting { Key = "RetrievalTuningSettings", Value = json });
            }
            else
            {
                setting.Value = json;
            }

            await context.SaveChangesAsync();
        }

        private static float CosineSimilarity(float[] vectorA, float[] vectorB)
        {
            if (vectorA.Length != vectorB.Length || vectorA.Length == 0) return 0;

            float dotProduct = 0;
            float normA = 0;
            float normB = 0;

            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                normA += vectorA[i] * vectorA[i];
                normB += vectorB[i] * vectorB[i];
            }

            if (normA == 0 || normB == 0) return 0;

            return dotProduct / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
        }

        /// <summary>
        /// Strip Ollama-style ":latest" / ":tag" suffix from a model name so vectors stored as
        /// "bge-m3" remain comparable to a current configured model called "bge-m3:latest".
        /// Tags only signal a specific version; the embedding model family is the same.
        /// </summary>
        private static string NormalizeModelName(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;
            var idx = modelName.IndexOf(':');
            return idx > 0 ? modelName[..idx] : modelName;
        }

        private IQueryable<TicketSemanticEmbedding> BuildCandidateQuery(ApplicationDbContext context, string targetModel, List<int>? statusIds, bool includeAllStatuses, int? excludeTicketId = null)
        {
            // Match by base model name (strip ":latest" / ":tag"). Stored vectors are tagged with
            // whatever ModelName the embedder reported at storage time — we want "bge-m3" and
            // "bge-m3:latest" to be considered the same family for candidate matching.
            var targetBase = NormalizeModelName(targetModel);

            var candidateQuery = context.TicketSemanticEmbeddings
                .Include(e => e.Ticket!)
                .ThenInclude(t => t.Status)
                .Include(e => e.Ticket!)
                .ThenInclude(t => t.Category)
                .Include(e => e.Ticket!)
                .ThenInclude(t => t.Priority)
                .Include(e => e.Ticket!)
                .ThenInclude(t => t.Source)
                .Include(e => e.Ticket!)
                .ThenInclude(t => t.Department)
                .Where(e => e.ModelName == targetModel
                         || e.ModelName == targetBase
                         || e.ModelName.StartsWith(targetBase + ":"))
                .AsQueryable();

            if (excludeTicketId.HasValue)
            {
                candidateQuery = candidateQuery.Where(e => e.TicketId != excludeTicketId.Value);
            }

            if (statusIds != null && statusIds.Count > 0 && !includeAllStatuses)
            {
                candidateQuery = candidateQuery.Where(e => e.Ticket != null && statusIds.Contains(e.Ticket.StatusId));
            }
            else if (!includeAllStatuses)
            {
                var terminal = new[] { "Resolved", "Closed" };
                candidateQuery = candidateQuery.Where(e => e.Ticket != null && e.Ticket.Status != null &&
                    terminal.Contains(e.Ticket.Status.Name));
            }

            return candidateQuery;
        }

        private static List<SemanticSearchMatch> ScoreCandidates(
            IEnumerable<TicketSemanticEmbedding> candidates,
            float[] targetVector,
            string normalizedQueryText,
            TicketLanguageProfile queryLanguage,
            int count,
            RetrievalTuningSettings settings,
            bool bypassThreshold = false)
        {
            var queryTerms = ExtractTerms(normalizedQueryText);
            // bypassThreshold=true means "return top-K by raw score, no precision floor". Used by
            // the conversational copilot, where a user typing "tickets about printer queue" wants
            // SOMETHING back even if cosine is modest — they can read scores and judge. The
            // legacy investigation tool keeps the threshold (false default) because it's
            // duplicate-finding and a low-confidence "duplicate" is worse than no match.
            var minimumSimilarityScore = bypassThreshold
                ? 0f
                : GetMinimumSimilarityScore(queryTerms.Count, queryLanguage, settings);

            return candidates
                .Where(c => c.Ticket != null && c.Vector != null && c.Vector.Length == targetVector.Length)
                .Select(c =>
                {
                    var vectorScore = Math.Max(0f, CosineSimilarity(targetVector, c.Vector));
                    var candidateText = NormalizeEmbeddingText(BuildLexicalCandidateText(c.Ticket!));
                    var candidateTerms = ExtractTerms(candidateText);
                    var lexicalScore = ComputeLexicalScore(queryTerms, candidateTerms);
                    var candidateLanguage = TicketLanguageDetector.DetectProfile(candidateText);
                    var languageBoost = ComputeLanguageBoost(queryLanguage, candidateLanguage, settings);
                    var score = CombineScores(vectorScore, lexicalScore, languageBoost, queryLanguage, candidateLanguage, settings);

                    return new SemanticSearchMatch
                    {
                        Ticket = c.Ticket!,
                        Score = score,
                        VectorScore = vectorScore,
                        LexicalScore = lexicalScore,
                        LanguageBoost = languageBoost
                    };
                })
                .Where(s => s.Score >= minimumSimilarityScore)
                .OrderByDescending(s => s.Score)
                .Take(count)
                .ToList();
        }

        private string BuildEmbeddingText(TicketContext context, RetrievalTuningSettings settings)
        {
            var parts = new List<string>
            {
                $"title: {context.Title}",
                $"description: {context.Description}"
            };

            if (context.RetrievalLanguage != TicketLanguageLabel.Unknown)
            {
                parts.Add($"language: {context.RetrievalLanguage}");
            }

            if (settings.IncludeTechnicalAssessment && !string.IsNullOrWhiteSpace(context.TechnicalAssessment))
            {
                parts.Add($"technical assessment: {context.TechnicalAssessment}");
            }

            var noCommentsLabel = _localizer.Get("NoComments", context.Language);
            if (settings.IncludeComments && !string.IsNullOrWhiteSpace(context.CommentsText) && context.CommentsText != noCommentsLabel)
            {
                parts.Add($"comments: {context.CommentsText}");
            }

            if (settings.IncludeAttachments)
            {
                var attachmentText = string.Join('\n', context.AttachmentClues
                    .Where(clue => clue.IsTextFile && !string.IsNullOrWhiteSpace(clue.ExtractedText))
                    .Select(clue => $"attachment {clue.FileName}: {clue.ExtractedText}"));

                if (!string.IsNullOrWhiteSpace(attachmentText))
                {
                    parts.Add(attachmentText);
                }
            }

            if (settings.IncludeMetadata)
            {
                if (!string.IsNullOrWhiteSpace(context.Category)) parts.Add($"category: {context.Category}");
                if (!string.IsNullOrWhiteSpace(context.Priority)) parts.Add($"priority: {context.Priority}");
                if (!string.IsNullOrWhiteSpace(context.Status)) parts.Add($"status: {context.Status}");
                if (!string.IsNullOrWhiteSpace(context.ProductArea)) parts.Add($"product area: {context.ProductArea}");
                if (!string.IsNullOrWhiteSpace(context.EnvironmentName)) parts.Add($"environment: {context.EnvironmentName}");
                if (!string.IsNullOrWhiteSpace(context.BrowserName)) parts.Add($"browser: {context.BrowserName}");
                if (!string.IsNullOrWhiteSpace(context.OperatingSystem)) parts.Add($"operating system: {context.OperatingSystem}");
                if (!string.IsNullOrWhiteSpace(context.ExternalSystemName)) parts.Add($"external system: {context.ExternalSystemName}");
                if (!string.IsNullOrWhiteSpace(context.ImpactScope)) parts.Add($"impact scope: {context.ImpactScope}");
                if (!string.IsNullOrWhiteSpace(context.PendingReason)) parts.Add($"pending reason: {context.PendingReason}");
                if (context.AffectedUsersCount.HasValue) parts.Add($"affected users: {context.AffectedUsersCount.Value}");
                if (!string.IsNullOrWhiteSpace(context.EscalationLevel)) parts.Add($"escalation level: {context.EscalationLevel}");
                if (context.IsSlaBreached) parts.Add("sla breached: yes");
                if (!string.IsNullOrWhiteSpace(context.Department)) parts.Add($"entity: {context.Department}");
                if (!string.IsNullOrWhiteSpace(context.Source)) parts.Add($"source: {context.Source}");
            }

            if (!string.IsNullOrWhiteSpace(context.RootCause))
            {
                parts.Add($"root cause: {context.RootCause}");
            }

            if (settings.IncludeResolutionSummary && !string.IsNullOrWhiteSpace(context.ResolutionSummary))
            {
                parts.Add($"resolution summary: {context.ResolutionSummary}");
            }

            if (!string.IsNullOrWhiteSpace(context.VerificationNotes))
            {
                parts.Add($"verification notes: {context.VerificationNotes}");
            }

            return string.Join("\n\n", parts);
        }

        private static string BuildLexicalCandidateText(Ticket ticket)
        {
            var parts = new List<string>
            {
                ticket.Title,
                ticket.Description,
                ticket.Category?.Name ?? string.Empty,
                ticket.Priority?.Name ?? string.Empty,
                ticket.Status?.Name ?? string.Empty,
                ticket.ProductArea ?? string.Empty,
                ticket.EnvironmentName ?? string.Empty,
                ticket.BrowserName ?? string.Empty,
                ticket.OperatingSystem ?? string.Empty,
                ticket.ExternalSystemName ?? string.Empty,
                ticket.ImpactScope ?? string.Empty,
                ticket.TechnicalAssessment ?? string.Empty,
                ticket.PendingReason ?? string.Empty,
                ticket.RootCause ?? string.Empty,
                ticket.ResolutionSummary ?? string.Empty,
                ticket.VerificationNotes ?? string.Empty,
                ticket.EscalationLevel ?? string.Empty,
                ticket.Department?.Name ?? string.Empty,
                ticket.Source?.Name ?? string.Empty
            };

            if (ticket.AffectedUsersCount.HasValue)
            {
                parts.Add(ticket.AffectedUsersCount.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (ticket.IsSlaBreached)
            {
                parts.Add("sla breached");
            }

            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static float ComputeLexicalScore(IReadOnlyCollection<string> queryTerms, IReadOnlyCollection<string> candidateTerms)
        {
            var matchedTerms = 0;
            foreach (var qTerm in queryTerms)
            {
                if (candidateTerms.Any(cTerm => 
                    string.Equals(qTerm, cTerm, StringComparison.OrdinalIgnoreCase) || 
                    IsNearMatch(qTerm, cTerm)))
                {
                    matchedTerms++;
                }
            }

            if (matchedTerms == 0) return 0f;
            return Math.Clamp((float)matchedTerms / queryTerms.Count, 0f, 1f);
        }

        private static bool IsNearMatch(string s1, string s2)
        {
            if (s1.Length < 4 || s2.Length < 4) return false;
            // Allow 1 character difference for words of length 4+
            return Math.Abs(s1.Length - s2.Length) <= 1 && ComputeLevenshteinDistance(s1, s2) <= 1;
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private static float CombineScores(
            float vectorScore,
            float lexicalScore,
            float languageBoost,
            TicketLanguageProfile queryLanguage,
            TicketLanguageProfile candidateLanguage,
            RetrievalTuningSettings settings)
        {
            var vectorWeight = settings.VectorWeight;
            var lexicalWeight = settings.LexicalWeight;

            if (queryLanguage.IsMixed || candidateLanguage.IsMixed)
            {
                vectorWeight = settings.MixedVectorWeight;
                lexicalWeight = settings.MixedLexicalWeight;
            }

            return Math.Clamp((vectorScore * vectorWeight) + (lexicalScore * lexicalWeight) + languageBoost, 0f, 1f);
        }

        private static float ComputeLanguageBoost(TicketLanguageProfile queryLanguage, TicketLanguageProfile candidateLanguage, RetrievalTuningSettings settings)
        {
            if (queryLanguage.Label == candidateLanguage.Label)
            {
                return settings.SameLanguageBoost;
            }

            if ((queryLanguage.IsMixed && SharesAnyScript(queryLanguage, candidateLanguage)) ||
                (candidateLanguage.IsMixed && SharesAnyScript(queryLanguage, candidateLanguage)))
            {
                return settings.MixedScriptBoost;
            }

            return 0f;
        }

        private static bool SharesAnyScript(TicketLanguageProfile first, TicketLanguageProfile second) =>
            (first.HasArabic && second.HasArabic) || (first.HasLatin && second.HasLatin);

        private static float GetMinimumSimilarityScore(int queryTermCount, TicketLanguageProfile languageProfile, RetrievalTuningSettings settings)
        {
            var threshold = settings.BaseThreshold;

            if (queryTermCount <= 3)
            {
                threshold += 0.04f;
            }
            else if (queryTermCount >= 12)
            {
                threshold -= 0.03f;
            }

            if (languageProfile.IsMixed)
            {
                threshold -= 0.015f;
            }

            return Math.Clamp(threshold, InvestigationHeuristics.SemanticSearchBaseThreshold, InvestigationHeuristics.SemanticSearchMaxThreshold);
        }

        private static HashSet<string> ExtractTerms(string normalizedText)
        {
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return normalizedText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsUsefulTerm)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsUsefulTerm(string term)
        {
            if (term.Length < 2)
            {
                return false;
            }

            if (InvestigationHeuristics.EnglishStopWords.Contains(term) || InvestigationHeuristics.ArabicStopWords.Contains(term))
            {
                return false;
            }

            return term.Any(char.IsLetterOrDigit);
        }

        private static string NormalizeEmbeddingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            var previousWasWhitespace = false;

            foreach (var ch in text.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsControl(ch))
                {
                    continue;
                }

                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                var lowered = char.ToLower(ch, CultureInfo.InvariantCulture);
                var normalized = lowered switch
                {
                    'أ' or 'إ' or 'آ' => 'ا',
                    'ؤ' => 'و',
                    'ئ' => 'ي',
                    'ة' => 'ه',
                    'ى' => 'ي',
                    '٠' or '۰' => '0',
                    '١' or '۱' => '1',
                    '٢' or '۲' => '2',
                    '٣' or '۳' => '3',
                    '٤' or '۴' => '4',
                    '٥' or '۵' => '5',
                    '٦' or '۶' => '6',
                    '٧' or '۷' => '7',
                    '٨' or '۸' => '8',
                    '٩' or '۹' => '9',
                    '،' => ',',
                    '؛' => ';',
                    '؟' => '?',
                    '\u0640' => ' ',
                    _ => lowered
                };

                if (char.IsPunctuation(normalized) || char.IsSeparator(normalized) || char.IsWhiteSpace(normalized))
                {
                    if (previousWasWhitespace)
                    {
                        continue;
                    }

                    sb.Append(' ');
                    previousWasWhitespace = true;
                    continue;
                }

                sb.Append(normalized);
                previousWasWhitespace = false;
            }

            return sb.ToString().Trim();
        }
    }
}
