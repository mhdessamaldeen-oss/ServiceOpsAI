namespace SuperAdminCopilot.Pipeline.Routing;

/// <summary>
/// Single up-front classifier that decides which branch of the pipeline a question takes.
/// Replaces the prior "cascade of try-handlers" pattern with one explicit dispatch point:
/// the router picks ONE intent, then the orchestrator jumps to the matching branch. No
/// silent fall-through — if the chosen branch can't fulfil, the orchestrator returns a
/// hard-fail message rather than secretly running a different branch.
///
/// <para><b>Hybrid strategy</b>: first try every registered <see cref="IRoutingProbe"/>
/// (deterministic, sub-millisecond, no LLM). When no probe claims the question with high
/// confidence the router falls back to <see cref="ILlmIntentClassifier"/> — a single small
/// LLM call (8-way classification, tiny prompt). The fallback fires only on genuinely
/// ambiguous questions, so common cases ("count of tickets", "hi", "what tables…") never
/// touch the LLM.</para>
/// </summary>
public interface IIntentRouter
{
    Task<RouterDecision> ClassifyAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// One probe contributed by a handler. Returns a <see cref="RouterDecision"/> when the
/// probe wants to claim the question, or <c>null</c> when it doesn't match. Implementations
/// MUST be cheap — regex + catalog lookup at worst, no DB calls, no LLM calls. The router
/// fans the question across every probe before deciding.
/// </summary>
public interface IRoutingProbe
{
    /// <summary>Human-readable name for trace breadcrumbs, e.g. "Conversational", "Tool".</summary>
    string Name { get; }

    /// <summary>Quick deterministic match — returns null when the probe doesn't claim the question.</summary>
    Task<RouterDecision?> ProbeAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// LLM-driven fallback classifier. Fires only when no <see cref="IRoutingProbe"/> claims
/// the question. Designed for a small local model: the system prompt is ≤ 600 chars and
/// asks for a single label token (one of <see cref="IntentLabel"/>). Stop sequence + max
/// tokens keep the call cheap.
/// </summary>
public interface ILlmIntentClassifier
{
    Task<RouterDecision> ClassifyAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// The eight possible routes. The orchestrator's branch dispatcher switches on this enum
/// to call exactly one handler. Adding a new label requires (a) adding a branch in the
/// orchestrator, (b) adding a handler that can fulfil it, and (c) adding a graph node so
/// the trace UI renders the new route.
/// </summary>
public enum IntentLabel
{
    /// <summary>"hi" / "thanks" / "what can you do" — handled by ConversationalHandler.</summary>
    Greeting = 0,

    /// <summary>"what is a ticket" / "explain priority" — handled by KnowledgeMatchHandler.</summary>
    Knowledge = 1,

    /// <summary>"tickets about login error" / "similar to TCK-X" — handled by SemanticSearchHandler.</summary>
    SemanticSearch = 2,

    /// <summary>"weather in Riyadh" / "currency rates" — handled by ToolHandler against CopilotToolDefinitions.</summary>
    Tool = 3,

    /// <summary>"what tables do we have" / "columns of Tickets" — handled by MetadataHandler.</summary>
    Metadata = 4,

    /// <summary>Real data question that needs SQL — flows through ShapeEngine then LlmPlanner.</summary>
    DataQuery = 5,

    /// <summary>"delete all closed tickets" / "show me the password" / "predict revenue" — refuse politely.</summary>
    Refuse = 6,

    /// <summary>"give me everything" / "show me stuff" — too vague; ask the user to clarify.</summary>
    Clarify = 7,
}

/// <summary>
/// One routing decision. Carries enough trace context that an admin can read the trace
/// UI and answer "why did we pick this branch?" without re-running the question.
/// </summary>
/// <param name="Intent">Which branch the orchestrator should dispatch to.</param>
/// <param name="Confidence">0.0–1.0. Probe matches are typically 0.9–1.0; LLM fallbacks
/// land 0.5–0.9. Below 0.5 the router emits <see cref="IntentLabel.Clarify"/>.</param>
/// <param name="Source">Which component decided. "<probe-name>" for a probe match,
/// "llm-classifier" for the LLM fallback.</param>
/// <param name="Reason">Free-text breadcrumb for the trace — what tipped the decision
/// ("matched greeting regex", "tool keyword score 12 vs 4 runner-up", etc.).</param>
/// <param name="RootEntity">Optional — for DataQuery decisions, the entity the router
/// already identified (passes through to the planner so it doesn't re-derive it).</param>
public sealed record RouterDecision(
    IntentLabel Intent,
    double Confidence,
    string Source,
    string Reason,
    string? RootEntity = null);
