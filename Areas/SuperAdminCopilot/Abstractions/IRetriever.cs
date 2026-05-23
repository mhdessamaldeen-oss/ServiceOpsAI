namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

public interface IRetriever
{
    /// <summary>
    /// Retrieve the schema slice relevant to the user's question.
    /// </summary>
    /// <param name="question">The user's question.</param>
    /// <param name="topK">Maximum number of primary tables to include.</param>
    /// <param name="tableHint">Optional table name resolved by the semantic rewriter. When set,
    /// the retriever guarantees this table (and its FK neighbors) appear in the slice even if
    /// keyword scoring would have ranked them lower. This prevents wrong-table schema slices
    /// when the user's phrasing doesn't mention the table name explicitly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SchemaSlice> RetrieveAsync(string question, int topK, string? tableHint = null, CancellationToken cancellationToken = default);
}
