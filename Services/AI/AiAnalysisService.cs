using ServiceOpsAI.Enums;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ServiceOpsAI.Services.Infrastructure;
using AutoMapper;
using AutoMapper.QueryableExtensions;

namespace ServiceOpsAI.Services.AI
{
    public class AiAnalysisService : IAiAnalysisService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly TicketContextPreparationService _contextPrep;
        private readonly TicketAiPromptBuilder _promptBuilder;
        private readonly IAiProviderFactory _providerFactory;
        private readonly ISemanticSearchService _semanticSearchService;
        private readonly ILocalizationService _localizer;
        private readonly ILogger<AiAnalysisService> _logger;
        private readonly IMapper _mapper;

        public AiAnalysisService(
            IDbContextFactory<ApplicationDbContext> contextFactory,
            TicketContextPreparationService contextPrep,
            TicketAiPromptBuilder promptBuilder,
            IAiProviderFactory providerFactory,
            ISemanticSearchService semanticSearchService,
            ILocalizationService localizer,
            ILogger<AiAnalysisService> logger,
            IMapper mapper)
        {
            _contextFactory = contextFactory;
            _contextPrep = contextPrep;
            _promptBuilder = promptBuilder;
            _providerFactory = providerFactory;
            _semanticSearchService = semanticSearchService;
            _localizer = localizer;
            _logger = logger;
            _mapper = mapper;
        }

        public async Task<TicketAiAnalysis?> GetLatestTicketAnalysisAsync(int ticketId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.CreatedOn)
                .FirstOrDefaultAsync();
        }

        public async Task<List<TicketAiAnalysis>> GetRunHistoryAsync(int ticketId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.CreatedOn)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<TicketAiAnalysis?> GetAnalysisByRunAsync(int ticketId, int runNumber)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.TicketAiAnalyses
                .Where(a => a.TicketId == ticketId)
                .OrderByDescending(a => a.CreatedOn)
                .Skip(Math.Max(0, runNumber - 1))
                .FirstOrDefaultAsync();
        }

        public async Task ResetInterruptedAnalysesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var stuck = await context.TicketAiAnalyses
                .Where(a => a.AnalysisStatus == AiAnalysisStatus.InProgress)
                .ToListAsync();

            foreach (var a in stuck)
            {
                a.AnalysisStatus = AiAnalysisStatus.Failed;
                a.Summary = "Analysis interrupted.";
            }

            if (stuck.Any()) await context.SaveChangesAsync();
        }

        public async Task<TicketAiAnalysis> RunTicketAnalysisAsync(int ticketId, string userId)
        {
            // For the lighter flow, we allow re-running which refreshes the record or creates a new one 
            // depending on the desired behavior. The user asked for "Save AI results in database".
            return await ExecuteAnalysisAsync(ticketId, userId);
        }

        public async Task<TicketAiAnalysis> RefreshTicketAnalysisAsync(int ticketId, string userId)
        {
            return await ExecuteAnalysisAsync(ticketId, userId);
        }

        private async Task AddExecutionLogAsync(TicketAiAnalysis analysis, string message, string level = "Info")
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var log = new TicketAiAnalysisLog
            {
                TicketAiAnalysisId = analysis.Id,
                Message = message,
                LogLevel = Enum.TryParse<AiLogLevel>(level, true, out var l) ? l : AiLogLevel.Info,
                CreatedOn = DateTime.UtcNow
            };
            context.TicketAiAnalysisLogs.Add(log);
            await context.SaveChangesAsync();
        }

        private async Task<TicketAiAnalysis> ExecuteAnalysisAsync(int ticketId, string userId)
        {
            var provider = _providerFactory.GetProviderForWorkload(ServiceOpsAI.Enums.AiWorkloadType.Analysis);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Create analysis record
            var analysis = new TicketAiAnalysis
            {
                TicketId = ticketId,
                CreatedBy = userId,
                CreatedOn = DateTime.UtcNow,
                AnalysisStatus = AiAnalysisStatus.InProgress,
                Summary = "Initializing analysis..."
            };
            
            using (var context = await _contextFactory.CreateDbContextAsync())
            {
                context.TicketAiAnalyses.Add(analysis);
                await context.SaveChangesAsync();
            }

            await AddExecutionLogAsync(analysis, $"Analysis started using {provider.ModelName}.");

            try
            {
                // 1. Prepare context
                await AddExecutionLogAsync(analysis, "Preparing ticket evidence...");
                var ticketContext = await _contextPrep.PrepareAsync(ticketId);

                // 1b. Tier-3.2 grounding — pull up to 3 similar resolved tickets and
                // attach them as anchor evidence in the prompt. We keep this best-effort:
                // if retrieval fails (embedder offline, no embeddings yet), the analysis
                // still runs without grounding rather than failing the whole pipeline.
                try
                {
                    var similar = await _semanticSearchService.GetRelatedTicketsAsync(
                        ticketId, count: 3,
                        restrictToTerminalStatuses: true,
                        requirePrecisionThreshold: true);
                    ticketContext.GroundingTickets = similar
                        .Select(m => new TicketGroundingEvidence
                        {
                            TicketNumber = m.Ticket.TicketNumber,
                            Title = m.Ticket.Title,
                            ResolutionSummary = m.Ticket.ResolutionSummary ?? "",
                            RootCause = m.Ticket.RootCause ?? "",
                            Score = m.Score
                        })
                        .ToList();
                    if (ticketContext.GroundingTickets.Count > 0)
                    {
                        await AddExecutionLogAsync(analysis,
                            $"Found {ticketContext.GroundingTickets.Count} similar resolved tickets to ground analysis.");
                    }
                }
                catch (Exception groundingEx)
                {
                    _logger.LogWarning(groundingEx, "Grounding evidence retrieval failed for ticket {TicketId}; analysis will run without anchor cases.", ticketId);
                }

                // 2. Build prompt
                var prompt = _promptBuilder.Build(ticketContext);
                analysis.InputPromptSize = prompt.Length;

                // 3. Call AI
                await AddExecutionLogAsync(analysis, "Consulting AI engine...");
                var result = await provider.GenerateAsync(prompt);
                sw.Stop();
                analysis.ProcessingDurationMs = sw.ElapsedMilliseconds;

                if (!result.Success)
                {
                    throw new Exception(result.Error ?? "Unknown AI provider error.");
                }

                // 4. Parse
                ParseAiResponse(result.ResponseText, analysis, provider);
                analysis.AnalysisStatus = AiAnalysisStatus.Success;
                await AddExecutionLogAsync(analysis, "Analysis completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI Analysis failed for ticket {TicketId}", ticketId);
                analysis.AnalysisStatus = AiAnalysisStatus.Failed;
                analysis.Summary = $"Analysis failed: {ex.Message}";
                await AddExecutionLogAsync(analysis, ex.Message, "Error");
            }

            analysis.ModelName = provider.ModelName;
            analysis.PromptVersion = TicketAiPromptBuilder.PromptVersion;
            analysis.LastRefreshedOn = DateTime.UtcNow;

            using (var context = await _contextFactory.CreateDbContextAsync())
            {
                context.TicketAiAnalyses.Update(analysis);
                await context.SaveChangesAsync();
            }

            return analysis;
        }

        private void ParseAiResponse(string responseText, TicketAiAnalysis analysis, IAiProvider provider)
        {
            try
            {
                var cleanJson = responseText.Trim();
                var startIdx = cleanJson.IndexOf('{');
                var endIdx = cleanJson.LastIndexOf('}');

                if (startIdx >= 0 && endIdx > startIdx)
                {
                    cleanJson = cleanJson[startIdx..(endIdx + 1)];
                }

                using var doc = JsonDocument.Parse(cleanJson);
                var root = doc.RootElement;

                analysis.Summary = GetJsonString(root, "summary", "No summary generated.");
                analysis.SuggestedClassification = GetJsonString(root, "suggestedClassification", "Unknown");
                analysis.SuggestedPriority = GetJsonString(root, "suggestedPriority", "Medium");
                analysis.KeyClues = GetJsonString(root, "keyClues", "");
                analysis.NextStepSuggestion = GetJsonString(root, "nextStepSuggestion", "");
                
                var clStr = GetJsonString(root, "confidenceLevel", "Low");
                analysis.ConfidenceLevel = Enum.TryParse<AiConfidenceLevel>(clStr, true, out var cl) ? cl : AiConfidenceLevel.Low;
            }
            catch (JsonException)
            {
                _logger.LogWarning("AI returned invalid JSON for ticket {TicketId}. Storing raw response as summary.", analysis.TicketId);
                analysis.Summary = responseText.Length > 1000 ? responseText[..1000] : responseText;
                analysis.AnalysisStatus = AiAnalysisStatus.Failed;
            }
        }

        private static string GetJsonString(JsonElement root, string propertyName, string defaultValue)
        {
            if (!root.TryGetProperty(propertyName, out var prop)) return defaultValue;

            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? defaultValue;

            if (prop.ValueKind == JsonValueKind.Array)
            {
                var values = prop.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => i.GetString())
                    .Where(s => s != null)
                    .ToList();
                return string.Join(", ", values);
            }

            return defaultValue;
        }
    }
}
