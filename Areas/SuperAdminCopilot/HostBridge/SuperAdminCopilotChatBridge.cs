namespace SuperAdminCopilot.HostBridge;

using System.Text.RegularExpressions;
using ServiceOpsAI.Models.AI;
using SuperAdminCopilot.Abstractions;
using NewCopilotRequest = SuperAdminCopilot.Models.CopilotRequest;

/// <summary>
/// Adapter between the host's legacy <see cref="CopilotChatRequest"/> /
/// <see cref="CopilotChatResponse"/> contract and the new copilot's
/// <see cref="ISuperAdminCopilot"/>. Lets the existing call sites
/// (AiAnalysisController.AskCopilot, CopilotAssessmentHandler.RunAssessmentAsync)
/// drop in a single line change to route through the new pipeline without
/// changing their downstream code that consumes <see cref="CopilotChatResponse"/>.
/// </summary>
public interface ISuperAdminCopilotChatBridge
{
    Task<CopilotChatResponse> AskAsync(CopilotChatRequest request, CancellationToken cancellationToken = default);
}

internal sealed class SuperAdminCopilotChatBridge : ISuperAdminCopilotChatBridge
{
    private readonly ISuperAdminCopilot _copilot;

    /// <summary>
    /// ToolHandler stamps either "-- Tool dispatched: &lt;key&gt; (..." or
    /// "-- Tool '&lt;key&gt;' needs more info ..." into the response's <see cref="CopilotResponse.Sql"/>
    /// slot. This regex pulls the toolKey from either form so the bridge can populate the
    /// host's <c>UsedTool</c> field accurately for assessment scoring. Compiled because the
    /// bridge runs on every request.
    /// </summary>
    private static readonly Regex ToolKeyExtractor = new(
        @"^--\s*Tool(?:\s+dispatched:|\s+')\s*'?(?<key>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SuperAdminCopilotChatBridge(ISuperAdminCopilot copilot) => _copilot = copilot;

    // The bridge serialises copilot row cells into AdminCopilotStructuredResultRow.Values (string-keyed).
    // The EX-accuracy checker on the gold side reads typed CLR objects from SqlDataReader (DateTime, decimal,
    // double, int, …) and canonicalises via NormalizeValue. If the bridge uses the default v.ToString()
    // here, DateTimes serialise with current-culture formatting ("5/14/2026 12:00:00 AM" or arabic-locale
    // variants) which NormalizeValue can't round-trip back to the gold side's ISO "O" format — every
    // verified-query case that returns a date silently fails EX-accuracy. We format invariantly here so
    // the two sides always converge.
    private static string FormatCellInvariant(object? v)
    {
        if (v is null) return string.Empty;
        if (v is DBNull) return string.Empty;
        if (v is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (v is DateTimeOffset dto) return DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Unspecified).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (v is decimal dec) return dec.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (v is double dbl) return dbl.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (v is float flt) return flt.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (v is bool bv) return bv ? "True" : "False";
        if (v is long lng) return lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v is int iv) return iv.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v is short sv) return sv.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v is byte by) return by.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString() ?? string.Empty;
    }

    public async Task<CopilotChatResponse> AskAsync(CopilotChatRequest request, CancellationToken cancellationToken = default)
    {
        // Translate the host's CopilotChatMessage history into the host-free PriorTurn list
        // the new copilot's IConversationContext consumes. We only forward turns whose role
        // is User/Assistant; any future tool/system roles are dropped because the entity-extraction
        // pass only walks user turns and the verbatim prior question only quotes user turns.
        IReadOnlyList<SuperAdminCopilot.Models.PriorTurn>? history = null;
        if (request.History is { Count: > 0 })
        {
            history = request.History
                .Select(m => new SuperAdminCopilot.Models.PriorTurn(
                    Content: m.Content ?? "",
                    IsUser: m.Role == ChatMessageRole.User))
                .ToList();
        }

        // Forward CaseCode + SourceSuite when present so the orchestrator's trace sink can
        // stamp them on the CopilotTraceHistory row. Assessment runs depend on this — without
        // it the trace has CaseCode=null and SourceSuite="super-admin-copilot" (the generic
        // fallback), and the assessment lab grid can't map traces back to suite + case.
        var result = await _copilot.AskAsync(
            new NewCopilotRequest(
                request.Question,
                ConversationId: request.SessionId?.ToString(),
                CaseCode: request.CaseCode,
                SourceSuite: request.SourceSuite,
                History: history,
                ExpectedSql: request.ExpectedSql,
                // Forward the chat client's SignalR connection id so the live progress sink
                // can target that specific connection. Without this, a brand-new chat's
                // ProgressUpdate events vanish — the client hasn't joined the chat_{sessionId}
                // group yet because the server only assigns the sessionId in the response.
                SignalRConnectionId: request.ConnectionId),
            cancellationToken);

        // Map the new copilot's compact response shape onto the legacy CopilotChatResponse.
        // Three downstream consumers read this shape:
        //   1. The chat UI — needs Answer, ModelName, Notes (SQL), TraceId, ExecutionDetails.Summary.
        //   2. The assessment scorer (CopilotAssessmentResult) — checks DetectedIntent, ResponseMode,
        //      UsedTool against the case's expectations. If we leave these blank/stub values
        //      ("super-admin-copilot", KnowledgeMatch), every assessment case that declares
        //      ExpectedIntent="DataQuery" fails, even when the new copilot answered perfectly.
        //   3. The investigation tree — already populated separately via the trace sink.
        //
        // So we derive the legacy fields from the new response shape:
        //   • SQL produced + no error  → intent=DataQuery, mode=DynamicTicketQuery
        //   • Error mentions clarification → intent=Clarification, mode=GeneralSupport
        //   • Error mentions unsafe/unsupported/refusal → intent=Unsupported, mode=GeneralSupport
        //   • Error otherwise → keep intent stubbed, but tag mode=GeneralSupport
        //   • No SQL, no error → general chat → intent=GeneralChat, mode=GeneralSupport
        // ToolHandler can dispatch external tools; we detect that from the trace steps.
        var hadSql = !string.IsNullOrEmpty(result.Sql);
        var hadError = !string.IsNullOrEmpty(result.Error);
        var lowerError = (result.Error ?? string.Empty).ToLowerInvariant();

        // ToolHandler signals "this was a tool question" two ways: a "tool-dispatch" step
        // (successful HTTP call) AND a SQL comment in result.Sql ("-- Tool dispatched: <key>")
        // that survives even when the dispatch fails. We treat either as evidence and parse the
        // toolKey out of the SQL comment so UsedTool carries the actual key the assessment case
        // declared in ExpectedToolKey — not a "external-tool" placeholder.
        var toolDispatched = result.Steps?.FirstOrDefault(s =>
            string.Equals(s.Kind, "tool-dispatch", StringComparison.OrdinalIgnoreCase));
        var toolKeyMatch = hadSql ? ToolKeyExtractor.Match(result.Sql!.TrimStart()) : Match.Empty;
        var isToolPath = toolDispatched is not null || toolKeyMatch.Success;
        var usedTool = toolKeyMatch.Success ? toolKeyMatch.Groups["key"].Value
                       : isToolPath ? "external-tool"
                       : "none";

        string detectedIntent;
        ResponseMode responseMode;
        if (isToolPath)
        {
            detectedIntent = CopilotIntentKind.ExternalToolQuery.ToString();
            responseMode = ResponseMode.KnowledgeMatch;
        }
        else if (hadSql && !hadError)
        {
            detectedIntent = CopilotIntentKind.DataQuery.ToString();
            responseMode = ResponseMode.StructuredTable;
        }
        else if (hadError && (lowerError.Contains("clarif") || lowerError.Contains("ambiguous")))
        {
            detectedIntent = "Clarification";
            responseMode = ResponseMode.Conversational;
        }
        else if (hadError && (
                              // PRIMARY: trust the orchestrator's trace tag for refusals. Any
                              // preflight-refused / out-of-scope outcome is by definition an
                              // Unsupported intent — much more reliable than substring matching
                              // on the user-facing refusal text (which is i18n-able and changes
                              // when the text catalog is edited).
                              string.Equals(result.Trace, "preflight-refused", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(result.Trace, "out-of-scope", StringComparison.OrdinalIgnoreCase)
                           // FALLBACK: legacy substring matchers, for any refusal path that
                           // sets a different Trace tag. Kept for backward compatibility.
                           || lowerError.Contains("unsupported") || lowerError.Contains("unsafe")
                           || lowerError.Contains("refusal") || lowerError.Contains("out of scope")
                           || lowerError.Contains("write intent") || lowerError.Contains("secret")
                           // Refusal error from WriteIntentGuard reads "This pipeline is read-only.
                           // The question expresses a 'delete' intent (en) which is not supported."
                           // The legacy single-word check ("unsupported") misses this two-word phrase,
                           // so we add the actual phrases the guards emit.
                           || lowerError.Contains("read-only") || lowerError.Contains("read only")
                           || lowerError.Contains("is not supported") || lowerError.Contains("expresses a")
                           // ScopeConfidenceGate / IntentClassifier refusal text from
                           // CopilotTextCatalog.PreflightOutOfScope. The phrase "answers from
                           // the database" was missing from the substring list, so OOS cases
                           // were being mis-tagged as DataQuery — the long-standing rubric
                           // quirk seen across suites 7/8/10.
                           || lowerError.Contains("prediction or opinion")
                           || lowerError.Contains("only answers from")))
        {
            detectedIntent = CopilotIntentKind.Unsupported.ToString();
            responseMode = ResponseMode.Conversational;
        }
        else if (!hadSql && !hadError)
        {
            detectedIntent = CopilotIntentKind.GeneralChat.ToString();
            responseMode = ResponseMode.Conversational;
        }
        else
        {
            // Errored but not a refusal — planner/compiler/executor failure. Tag as DataQuery
            // attempt that failed so the case still scores against ExpectedIntent=DataQuery and
            // the assessment can pin the failure to a stage via ExpectedFailedStage.
            detectedIntent = CopilotIntentKind.DataQuery.ToString();
            responseMode = ResponseMode.StructuredTable;
        }

        var response = new CopilotChatResponse
        {
            TraceId = result.TraceId,
            Question = request.Question,
            Answer = string.IsNullOrEmpty(result.Error) ? result.Reply : $"{result.Reply}\n\nError: {result.Error}",
            ModelName = "super-admin-copilot",
            Notes = result.Sql ?? string.Empty,
            ResponseMode = responseMode,
            UsedTool = usedTool,
            ExecutionDetails = new AdminCopilotExecutionDetails
            {
                Summary = string.IsNullOrEmpty(result.Error)
                    ? $"super-admin-copilot: {result.RowCount} row(s) in {result.Trace ?? "ok"}"
                    : $"super-admin-copilot failed at stage '{result.Trace}': {result.Error}",
                DetectedIntent = detectedIntent,
                // RouteReason carries the terminal stage name — assessments check this when
                // the case sets ExpectedFailedStage (e.g. "expected Validator failure, got Executor").
                RouteReason = result.Trace ?? (hadError ? "error" : "ok"),
                Provenance = result.Provenance,
                Confidence = result.Confidence,
            },
        };

        // Surface the result rows + column names in the legacy shape so the assessment scorer
        // can read row count + column shape directly off the response. Each row is flattened
        // to a string dictionary because AdminCopilotStructuredResultRow.Values is string-keyed.
        if (result.Rows is { Count: > 0 } rows)
        {
            response.StructuredColumns = rows[0].Keys.ToList();
            response.StructuredRows = rows.Select(row =>
            {
                var rr = new AdminCopilotStructuredResultRow();
                foreach (var (k, v) in row)
                    rr.Values[k] = FormatCellInvariant(v);
                return rr;
            }).ToList();
        }

        if (result.SuggestedPrompts is { Count: > 0 } prompts)
            response.SuggestedPrompts = prompts.ToList();

        if (result.SimilarEntities is { Count: > 0 } hits)
        {
            // The host's existing card view is a `CopilotTicketCitation` shape. We map only hits
            // whose EntityType is "Ticket" into it — the generic SemanticSearchHit now also carries
            // hits from other indexed corpora (Order, KB article) which the host card view doesn't
            // render. A future host with multi-entity cards can dispatch by EntityType here.
            response.SimilarTickets = hits
                .Where(h => string.Equals(h.EntityType, "Ticket", StringComparison.OrdinalIgnoreCase))
                .Select(h => new CopilotTicketCitation
                {
                    TicketId = int.TryParse(h.EntityKey, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var tid) ? tid : 0,
                    TicketNumber = h.NaturalKey,
                    Title = h.DisplayLabel,
                    Score = h.Score,
                    // ResolutionSummary / RootCause / Status not captured in SemanticSearchHit; the
                    // host card view degrades gracefully when these are empty.
                })
                .ToList();
        }

        return response;
    }
}
