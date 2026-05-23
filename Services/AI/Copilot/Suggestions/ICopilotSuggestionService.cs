namespace AISupportAnalysisPlatform.Services.AI.Copilot.Suggestions;

/// <summary>
/// Returns a small, diverse set of example prompts the system is known to handle correctly.
/// Sourced from the curated assessment catalog so the same questions are guaranteed to work
/// against the current pipeline. Used by clarification + system-error responses to give the
/// user a copy-paste-ready next step instead of a dead-end "please clarify" nudge.
/// </summary>
public interface ICopilotSuggestionService
{
    /// <summary>
    /// Pick up to <paramref name="count"/> example prompts, evenly spread across difficulty
    /// (Easy / Medium / Hard) so the user gets a sample of what the system can do — not 4 trivial
    /// "show all tickets" lookalikes.
    /// </summary>
    Task<IReadOnlyList<string>> GetSuggestionsAsync(int count = 4, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live type-ahead: given what the user has typed so far, return up to <paramref name="max"/>
    /// catalog questions whose tokens overlap (stem-aware) with the partial input. Used by the
    /// chat input's autocomplete dropdown to surface real, working questions as the user types.
    /// Empty <paramref name="partial"/> returns the same stratified set as GetSuggestionsAsync.
    /// </summary>
    Task<IReadOnlyList<TypeAheadSuggestion>> GetTypeAheadAsync(string partial, int max = 6, CancellationToken cancellationToken = default);
}

/// <summary>
/// One suggestion shown in the autocomplete dropdown.
/// </summary>
public sealed record TypeAheadSuggestion(string Question, string Category, string Difficulty);
