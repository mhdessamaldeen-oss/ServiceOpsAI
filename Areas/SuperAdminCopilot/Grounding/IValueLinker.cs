namespace SuperAdminCopilot.Grounding;

using SuperAdminCopilot.Schema;

/// <summary>
/// Pre-LLM value linker. Scans the question text for tokens that match actual values in the
/// database (lookup-style tables: Regions, ServiceTypes, TicketStatuses, etc.) and returns the
/// resolved bindings. The bindings are then included in the LLM prompt as ground-truth
/// constraints — the LLM is told <c>"WHERE Regions.NameEn = 'Damascus' is required"</c> instead
/// of guessing how to express the constraint.
///
/// <para>Conceptually maps to the "value linking" stage in ValueNet / CHESS / E-SQL. The
/// implementation here is exact-substring + whole-word; fuzzy linking (edit-distance) is a
/// follow-up.</para>
/// </summary>
public interface IValueLinker
{
    Task<IReadOnlyList<ValueLinkBinding>> LinkAsync(
        string question,
        IReadOnlyList<InferredTable> linkedTables,
        CancellationToken cancellationToken = default);
}
