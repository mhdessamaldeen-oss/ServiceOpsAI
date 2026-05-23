using ServiceOpsAI.Models.AI;
using ServiceOpsAI.Data;
using ServiceOpsAI.Models;
using Microsoft.EntityFrameworkCore;
using ServiceOpsAI.Services.Infrastructure;
using ServiceOpsAI.Enums;

namespace ServiceOpsAI.Services.AI
{
    /// <summary>
    /// Collects and prepares all ticket evidence (description, comments, attachment text/logs)
    /// into a structured context object for the AI prompt builder.
    /// </summary>
    public class TicketContextPreparationService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly IWebHostEnvironment _env;
        private readonly ILocalizationService _localizer;
        private const int MaxAttachmentTextLength = 2000;    // per file
        private const int MaxTotalAttachmentText = 4000;     // all files combined
        private const int MaxCommentTextLength = 2000;       // all comments combined

        public TicketContextPreparationService(IDbContextFactory<ApplicationDbContext> contextFactory, IWebHostEnvironment env, ILocalizationService localizer)
        {
            _contextFactory = contextFactory;
            _env = env;
            _localizer = localizer;
        }

        public async Task<TicketContext> PrepareAsync(int ticketId, CancellationToken cancellationToken = default)
        {
            using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var ticket = await context.Tickets
                .Include(t => t.Category)
                .Include(t => t.Priority)
                .Include(t => t.Status)
                .Include(t => t.Department)
                .Include(t => t.Source)
                .Include(t => t.AssignedToUser)
                .Include(t => t.CreatedByUser)
                .Include(t => t.Comments).ThenInclude(c => c.CreatedByUser)
                .Include(t => t.Attachments)
                .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

            if (ticket == null) throw new ArgumentException($"Ticket {ticketId} not found");

            var languageProfile = TicketLanguageDetector.DetectProfile(
                ticket.Title,
                ticket.Description,
                ticket.TechnicalAssessment,
                ticket.PendingReason,
                ticket.ResolutionSummary,
                ticket.RootCause);
            var language = languageProfile.PreferredLocalization;

            var ctx = new TicketContext
            {
                TicketNumber = ticket.TicketNumber,
                Title = ticket.Title,
                Description = ticket.Description,
                Category = ticket.Category?.Name ?? _localizer.Get(nameof(SystemStrings.Unknown), language),
                Priority = ticket.Priority?.Name ?? _localizer.Get(nameof(SystemStrings.Unknown), language),
                Status = ticket.Status?.Name ?? _localizer.Get(nameof(SystemStrings.Unknown), language),
                Department = ticket.Department?.Name ?? _localizer.Get(nameof(SystemStrings.General), language),
                Source = ticket.Source?.Name ?? _localizer.Get(nameof(SystemStrings.Unknown), language),
                ProductArea = ticket.ProductArea ?? "",
                EnvironmentName = ticket.EnvironmentName ?? "",
                BrowserName = ticket.BrowserName ?? "",
                OperatingSystem = ticket.OperatingSystem ?? "",
                ExternalSystemName = ticket.ExternalSystemName ?? "",
                ExternalReferenceId = ticket.ExternalReferenceId ?? "",
                ImpactScope = ticket.ImpactScope ?? "",
                AffectedUsersCount = ticket.AffectedUsersCount,
                TechnicalAssessment = ticket.TechnicalAssessment ?? "",
                EscalationLevel = ticket.EscalationLevel ?? "",
                RootCause = ticket.RootCause ?? "",
                VerificationNotes = ticket.VerificationNotes ?? "",
                PendingReason = ticket.PendingReason ?? "",
                AssignedTo = ticket.AssignedToUser?.FullName ?? _localizer.Get(nameof(SystemStrings.Unassigned), language),
                CreatedBy = ticket.CreatedByUser?.FullName ?? _localizer.Get(nameof(SystemStrings.Unknown), language),
                CreatedAt = ticket.CreatedAt,
                IsSlaBreached = ticket.IsSlaBreached,
                ResolutionSummary = ticket.ResolutionSummary ?? "",
                Language = language,
                RetrievalLanguage = languageProfile.Label
            };

            // Prepare comments
            ctx.CommentsText = PrepareComments(ticket.Comments, ctx.Language);

            // Prepare attachment text content
            ctx.AttachmentClues = await PrepareAttachmentTextAsync(ticket.Attachments, ctx.Language);

            return ctx;
        }

        private string PrepareComments(ICollection<TicketComment> comments, string language)
        {
            if (!comments.Any()) return _localizer.Get("NoComments", language);

            var orderedComments = comments.OrderBy(c => c.CreatedAt).ToList();
            var sb = new System.Text.StringBuilder();
            int totalLen = 0;

            foreach (var c in orderedComments)
            {
                var author = c.CreatedByUser?.FullName ?? c.CreatedByUserId;
                var entry = $"[{c.CreatedAt:yyyy-MM-dd HH:mm}] {author}: {c.Content}\n";

                if (totalLen + entry.Length > MaxCommentTextLength)
                {
                    sb.AppendLine(string.Format(_localizer.Get("CommentsTruncated", language), orderedComments.Count - orderedComments.IndexOf(c)));
                    break;
                }
                sb.Append(entry);
                totalLen += entry.Length;
            }

            return sb.ToString().TrimEnd();
        }

        private async Task<List<AttachmentClue>> PrepareAttachmentTextAsync(ICollection<TicketAttachment> attachments, string language)
        {
            var clues = new List<AttachmentClue>();
            int totalTextLen = 0;

            // Readable text-based content types
            var readableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".log", ".json", ".xml", ".csv", ".sql", ".html", ".htm",
                ".yaml", ".yml", ".md", ".ini", ".cfg", ".conf", ".har", ".pcap"
            };

            foreach (var att in attachments)
            {
                if (totalTextLen >= MaxTotalAttachmentText) break;

                var ext = Path.GetExtension(att.FileName);
                if (!readableExtensions.Contains(ext)) 
                {
                    // Non-text file: just note its existence
                    clues.Add(new AttachmentClue
                    {
                        FileName = att.FileName,
                        ContentType = att.ContentType,
                        FileSize = att.FileSize,
                        ExtractedText = null,
                        IsTextFile = false
                    });
                    continue;
                }

                // Try to read the file content
                var filePath = Path.Combine(_env.WebRootPath, att.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(filePath))
                {
                    clues.Add(new AttachmentClue
                    {
                        FileName = att.FileName,
                        ContentType = att.ContentType,
                        FileSize = att.FileSize,
                        ExtractedText = _localizer.Get("FileNotFound", language),
                        IsTextFile = true
                    });
                    continue;
                }

                try
                {
                    var rawText = await File.ReadAllTextAsync(filePath);
                    var processed = ProcessLogContent(rawText, att.FileName, language);
                    
                    // Truncate if too long
                    if (processed.Length > MaxAttachmentTextLength)
                    {
                        processed = processed[..MaxAttachmentTextLength] + $"\n" + string.Format(_localizer.Get("TruncatedResult", language), rawText.Length);
                    }

                    totalTextLen += processed.Length;

                    clues.Add(new AttachmentClue
                    {
                        FileName = att.FileName,
                        ContentType = att.ContentType,
                        FileSize = att.FileSize,
                        ExtractedText = processed,
                        IsTextFile = true
                    });
                }
                catch
                {
                    clues.Add(new AttachmentClue
                    {
                        FileName = att.FileName,
                        ContentType = att.ContentType,
                        FileSize = att.FileSize,
                        ExtractedText = _localizer.Get("ErrorReadingFile", language),
                        IsTextFile = true
                    });
                }
            }

            return clues;
        }

        /// <summary>
        /// Intelligently processes log content to preserve important evidence while reducing noise.
        /// Keeps ERROR/WARN/FATAL lines, stack traces, status codes, and trims repeated INFO noise.
        /// </summary>
        private string ProcessLogContent(string rawText, string fileName, string language)
        {
            var lines = rawText.Split('\n');
            if (lines.Length <= 80) return rawText; // Small files: keep as-is

            var importantLines = new List<string>();
            var noiseCount = 0;
            var seenPatterns = new HashSet<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Always keep: errors, warnings, fatal, exceptions, stack traces
                if (IsImportantLine(trimmed))
                {
                    if (noiseCount > 0)
                    {
                        importantLines.Add(string.Format(_localizer.Get("RoutineLinesOmitted", language), noiseCount));
                        noiseCount = 0;
                    }
                    importantLines.Add(trimmed);
                }
                else
                {
                    // Check for duplicate noise patterns
                    var signature = GetLineSignature(trimmed);
                    if (seenPatterns.Contains(signature))
                    {
                        noiseCount++;
                        continue;
                    }
                    seenPatterns.Add(signature);

                    // Keep first few info lines, then start truncating
                    if (importantLines.Count < 10 || noiseCount == 0)
                    {
                        importantLines.Add(trimmed);
                    }
                    else
                    {
                        noiseCount++;
                    }
                }
            }

            if (noiseCount > 0)
                importantLines.Add(string.Format(_localizer.Get("AdditionalLinesOmitted", language), noiseCount));

            return string.Join("\n", importantLines);
        }

        private static bool IsImportantLine(string line)
        {
            return line.Contains("[ERROR", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[WARN",  StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[FATAL", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("THREAT DETECTED", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("MALWARE", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("QUARANTINE", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("SECURITY", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("BREACH", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("DENIED", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("  at ", StringComparison.Ordinal) || // stack trace lines
                   line.Contains("--- ", StringComparison.Ordinal) ||  // stack trace separator
                   line.Contains("Root cause", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("POST-INCIDENT", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("ESCALATION", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("EXFILTRATION", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLineSignature(string line)
        {
            // Strip timestamps and numeric IDs to detect repeated log patterns
            var sig = System.Text.RegularExpressions.Regex.Replace(line, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+", "TS");
            sig = System.Text.RegularExpressions.Regex.Replace(sig, @"\d+", "N");
            return sig.Length > 80 ? sig[..80] : sig;
        }
    }

    // ─── Context DTOs ────────────────────────────────────────────────────────────

    public class TicketContext
    {
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string Priority { get; set; } = "";
        public string Status { get; set; } = "";
        public string Department { get; set; } = "";
        public string Source { get; set; } = "";
        public string ProductArea { get; set; } = "";
        public string EnvironmentName { get; set; } = "";
        public string BrowserName { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public string ExternalSystemName { get; set; } = "";
        public string ExternalReferenceId { get; set; } = "";
        public string ImpactScope { get; set; } = "";
        public int? AffectedUsersCount { get; set; }
        public string TechnicalAssessment { get; set; } = "";
        public string EscalationLevel { get; set; } = "";
        public string RootCause { get; set; } = "";
        public string VerificationNotes { get; set; } = "";
        public string PendingReason { get; set; } = "";
        public string AssignedTo { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool IsSlaBreached { get; set; }
        public string ResolutionSummary { get; set; } = "";
        public string CommentsText { get; set; } = "";
        public string Language { get; set; } = "English";
        public TicketLanguageLabel RetrievalLanguage { get; set; } = TicketLanguageLabel.English;
        public List<AttachmentClue> AttachmentClues { get; set; } = new();
        // Tier 3.2 — populated by AiAnalysisService when retrieval grounding is enabled.
        // Empty list means "analyze without grounding evidence" (the legacy behavior).
        public List<TicketGroundingEvidence> GroundingTickets { get; set; } = new();
    }

    public class AttachmentClue
    {
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long FileSize { get; set; }
        public string? ExtractedText { get; set; }
        public bool IsTextFile { get; set; }
    }

    public class TicketGroundingEvidence
    {
        public string TicketNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string ResolutionSummary { get; set; } = "";
        public string RootCause { get; set; } = "";
        public float Score { get; set; }
    }
}
