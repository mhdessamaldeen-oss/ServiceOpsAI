namespace AnalystAgent.Grounding;

/// <summary>
/// Pre-LLM grounding orchestrator. Given a question, returns a <see cref="QuestionGroundingContext"/>
/// that resolves everything we can deterministically — schema links, value links, temporal slots,
/// natural keys, intent shape, lifecycle verbs — BEFORE the LLM ever sees the question.
///
/// <para>This is the core of the principled redesign (DIN-SQL / CHESS-style pipeline): instead of
/// generating ungrounded SQL and patching it, we ground first and let the LLM wire up the
/// pre-resolved pieces.</para>
/// </summary>
public interface IQuestionGrounder
{
    Task<QuestionGroundingContext> GroundAsync(
        string question,
        IReadOnlyList<AnalystAgent.Schema.InferredTable> linkedTables,
        CancellationToken cancellationToken = default);
}
