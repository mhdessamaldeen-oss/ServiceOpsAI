namespace AnalystAgent.Configuration;

/// <summary>
/// Role-keyword boosts that the semantic retriever appends to each table's embedding text.
/// Lets operators tune (or translate) the lookup/bridge/person/fact biases without recompiling.
///
/// <para>Sourced from <c>Areas/AnalystAgent/Configuration/embedding-keywords.json</c>;
/// hot-reloadable via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>.</para>
///
/// <para>Defaults match the previous hardcoded values inside
/// <c>SchemaSemanticRetriever.BuildEmbeddingText</c> exactly — behavior is byte-identical
/// until the operator changes the file.</para>
/// </summary>
public sealed class EmbeddingKeywordsOptions
{
    public const string SectionName = "EmbeddingKeywords";

    /// <summary>Boost appended when <c>InferredTable.Flags.IsLookup</c> is true.</summary>
    public string Lookup { get; set; } = "lookup reference values options types";

    /// <summary>Boost appended when <c>InferredTable.Flags.IsBridge</c> is true.</summary>
    public string Bridge { get; set; } = "association relationship mapping link";

    /// <summary>Boost appended when <c>InferredTable.Flags.IsPerson</c> is true.</summary>
    public string Person { get; set; } = "users people members accounts";

    /// <summary>Boost for fact / domain tables (default empty — historically removed because
    /// it biased fact tables to outrank lookups on short generic queries; configurable now).</summary>
    public string Fact { get; set; } = "";
}
