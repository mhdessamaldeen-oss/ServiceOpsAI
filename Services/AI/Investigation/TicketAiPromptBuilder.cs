using System.Text;

namespace AISupportAnalysisPlatform.Services.AI
{
    /// <summary>
    /// Builds the final analysis prompt from a prepared TicketContext.
    /// Outputs instructions for structured JSON response from the LLM based on the lighter admin-only analysis model.
    /// Optimized for local models to prevent hallucination and improve reliability.
    /// </summary>
    public class TicketAiPromptBuilder
    {
        public const string PromptVersion = "4.0";

        public string Build(TicketContext ctx)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are a technical support analyst assistant.");
            sb.AppendLine("Your job is to read the ticket details and provide a brief, practical summary and extract key clues.");
            sb.AppendLine();
            sb.AppendLine("## Core Focus");
            sb.AppendLine("- Produce a concise overall summary (1-2 sentences).");
            sb.AppendLine("- Identify 2-3 strongest technical clues or signals from the description, comments, or logs.");
            sb.AppendLine("- Suggest a classification and priority level.");
            sb.AppendLine("- Suggest a single, actionable next step for the human reviewer.");
            sb.AppendLine();
            sb.AppendLine("## Local Model Optimization Rules");
            sb.AppendLine("- Do NOT provide long-form investigation or deep analysis.");
            sb.AppendLine("- Do NOT speculate beyond the provided evidence.");
            sb.AppendLine("- Keep all responses brief and factual.");
            sb.AppendLine($"- Detected ticket language: {ctx.Language}. Always provide final JSON values in English.");
            sb.AppendLine();

            // ── Ticket metadata ──
            sb.AppendLine("=== TICKET METADATA ===");
            sb.AppendLine($"Title: {ctx.Title}");
            sb.AppendLine($"Current Category: {ctx.Category}");
            sb.AppendLine($"Current Priority: {ctx.Priority}");
            sb.AppendLine($"Status: {ctx.Status}");
            sb.AppendLine();

            // ── Ticket description ──
            sb.AppendLine("=== TICKET DESCRIPTION ===");
            sb.AppendLine(ctx.Description);
            sb.AppendLine();

            // ── Comments ──
            if (!string.IsNullOrWhiteSpace(ctx.CommentsText) && ctx.CommentsText != "(No comments on this ticket)")
            {
                sb.AppendLine("=== COMMENTS ===");
                sb.AppendLine(ctx.CommentsText);
                sb.AppendLine();
            }

            // ── Attachment evidence ──
            // Tier 3.1 — when we truncate an attachment, the marker now reports the
            // exact number of characters dropped. The previous "... [TRUNCATED]" gave
            // the model no signal about HOW much was hidden, so it could not surface
            // "the attachment is too large to fully analyze; please share an excerpt".
            if (ctx.AttachmentClues.Count > 0)
            {
                var textAttachments = ctx.AttachmentClues.Where(c => c.IsTextFile && !string.IsNullOrWhiteSpace(c.ExtractedText)).ToList();
                if (textAttachments.Any())
                {
                    sb.AppendLine("=== ATTACHMENT EVIDENCE ===");
                    foreach (var clue in textAttachments)
                    {
                        sb.AppendLine($"--- {clue.FileName} ---");
                        const int attachmentBudget = 1500;
                        var fullLength = clue.ExtractedText!.Length;
                        string text;
                        if (fullLength > attachmentBudget)
                        {
                            var dropped = fullLength - attachmentBudget;
                            text = clue.ExtractedText[..attachmentBudget]
                                 + $"\n\n[…TRUNCATED — {dropped:N0} additional characters not shown to keep the prompt small. Ask the user to attach a focused excerpt if relevant.]";
                        }
                        else
                        {
                            text = clue.ExtractedText;
                        }
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
            }

            // ── Tier 3.2 — Similar-ticket grounding evidence ──
            // When the context preparation step found resolved tickets that look
            // like this one, surface their resolutions to the model as factual
            // anchor points. Drastically reduces "key clue" hallucinations because
            // the model has concrete prior outcomes to lean on, not just imagination.
            if (ctx.GroundingTickets.Count > 0)
            {
                sb.AppendLine("=== SIMILAR RESOLVED TICKETS (grounding evidence) ===");
                sb.AppendLine("Use these as the primary basis for clues and the next-step suggestion. Cite by ticket number when relevant.");
                foreach (var g in ctx.GroundingTickets)
                {
                    sb.AppendLine($"--- {g.TicketNumber} (similarity {g.Score:P0}) ---");
                    if (!string.IsNullOrWhiteSpace(g.Title)) sb.AppendLine($"Title: {g.Title}");
                    if (!string.IsNullOrWhiteSpace(g.RootCause)) sb.AppendLine($"Root cause: {g.RootCause}");
                    if (!string.IsNullOrWhiteSpace(g.ResolutionSummary)) sb.AppendLine($"Resolution: {g.ResolutionSummary}");
                    sb.AppendLine();
                }
            }

            // ── Response format instructions ──
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Based on the evidence above, return valid JSON ONLY with this exact structure:");
            sb.AppendLine(@"{
  ""summary"": ""concise overall summary (1-2 English sentences)"",
  ""keyClues"": [""strongest clue 1"", ""strongest clue 2""],
  ""suggestedClassification"": ""best-fit category (one of: Authentication, Access, Performance, UI/UX, Data, Reporting, Hardware, Other)"",
  ""suggestedPriority"": ""High | Medium | Low"",
  ""nextStepSuggestion"": ""short actionable next step"",
  ""confidenceLevel"": ""High | Medium | Low""
}");
            sb.AppendLine();
            sb.AppendLine("## Final Constraints:");
            sb.AppendLine("- Respond ONLY with the JSON object. No preamble, no markdown code fences.");
            sb.AppendLine("- All field values MUST be in English.");
            sb.AppendLine("- Use 'Unknown' or 'Low' if evidence is weak.");

            return sb.ToString();
        }
    }
}
