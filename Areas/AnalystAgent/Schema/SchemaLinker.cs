namespace AnalystAgent.Schema;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Pipeline.EntityResolution;
using AnalystAgent.Retrieval;
using AnalystAgent.Semantic;

/// <summary>
/// THE single schema-linking component: given a question, return the tight set of tables to put in
/// front of the SQL generator. It combines three SIMILARITY signals with the right precedence —
///   (1) lexical NAME / singular / camelCase-tail / SYNONYM match (high precision: "users" → AspNetUsers,
///       "تذاكر" → Tickets via the synonym list),
///   (2) TRIGRAM fuzzy similarity (typo / near-miss tolerance, Arabic-safe), and
///   (3) EMBEDDING cosine via <see cref="ISchemaSemanticRetriever"/> (semantic recall: "staff" → AspNetUsers)
/// — then takes the matched-set FK CLOSURE (the lookups a matched table references + the bridge tables
/// that connect two matched tables) so joins are expressible.
///
/// <para>Anchors (signals 1+2) are high-precision and PRIMARY; the embedder (signal 3, now backed by a
/// real bge-m3 model) is the RECALL fallback used when the question names no table — keeping the slice
/// tight for a 7B (RSL-SQL: small models want low-noise slices). This replaces the scattered keyword /
/// camelCase / retrieval-fallback / FK-budget logic that used to live in DirectAnalystPath.</para>
/// </summary>
public interface ISchemaLinker
{
    Task<IReadOnlyList<string>> LinkAsync(string question, CancellationToken cancellationToken = default);

    /// <summary>True when the question carries a DECISIVE in-scope signal — it lexically anchors a
    /// known table (name/synonym/tail) OR contains a token matching a configured natural-key format
    /// (e.g. "TKT-2026-00050"). Used to override a shaky classifier "out-of-scope" verdict so valid
    /// lookups / entity questions on non-embedded tables aren't falsely refused. No embedding call.</summary>
    bool HasInScopeSignal(string question);
}

internal sealed class SchemaLinker : ISchemaLinker
{
    private readonly ISchemaKnowledge _knowledge;
    private readonly ISchemaSemanticRetriever _retriever;
    private readonly IEntityCatalog _catalog;
    private readonly IForeignKeyGraph _fkGraph;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IOptions<Configuration.AnalystOptions> _options;
    private readonly ILogger<SchemaLinker> _logger;

    /// <summary>Compiled natural-key formats from semantic-layer.json (e.g. Tickets TKT-…, Bills ELEC-…),
    /// built once. A question token matching one is a hard in-scope signal.</summary>
    private readonly Lazy<IReadOnlyList<Regex>> _naturalKeyFormats;

    /// <summary>Global VALUE→TABLE index over distinctive ENTITY-SUBTYPE values, built once. Maps a lowercased
    /// value ("transformer") to its owning (Table, Column) so a question naming an entity-via-its-type anchors
    /// the right table. Restricted to entity-subtype columns (see AnalystOptions.EntitySubtypeColumnSuffixes)
    /// so attribute values ("resolved"/"high") never steer table selection. Empty dict if disabled / build fails.</summary>
    private readonly Lazy<IReadOnlyDictionary<string, (string Table, string Column)>> _enumValueIndex;

    private const double FuzzyAnchorFloor = 0.70;   // trigram for a TYPO'd table name
    private const float EmbedFallbackFloor = 0.30f; // min cosine to trust an embedding-only match
    private const int MaxBridgePathHops = 4;        // cap transitive FK-path bridging (keeps slice tight)

    public SchemaLinker(
        ISchemaKnowledge knowledge,
        ISchemaSemanticRetriever retriever,
        IEntityCatalog catalog,
        IForeignKeyGraph fkGraph,
        ISemanticLayer semanticLayer,
        IOptions<Configuration.AnalystOptions> options,
        ILogger<SchemaLinker> logger)
    {
        _knowledge = knowledge;
        _retriever = retriever;
        _catalog = catalog;
        _fkGraph = fkGraph;
        _semanticLayer = semanticLayer;
        _options = options;
        _logger = logger;
        _naturalKeyFormats = new Lazy<IReadOnlyList<Regex>>(BuildNaturalKeyFormats);
        _enumValueIndex = new Lazy<IReadOnlyDictionary<string, (string, string)>>(
            () => BuildEnumValueIndex(_catalog, _options.Value.EntitySubtypeColumnSuffixes, _logger));
    }

    /// <summary>Prime the value→table index (the one-time probe of entity-subtype columns) at warmup so the
    /// first real question doesn't pay the build cost. Safe to call repeatedly (Lazy builds once).</summary>
    public int PrimeEnumValueIndex() => _enumValueIndex.Value.Count;

    // Generic, schema-agnostic non-entity tokens — never an entity the user counts. NOT per-table business
    // vocab (it's the same handful for any schema), so it stays portable.
    private static readonly HashSet<string> GenericValueStopWords = new(StringComparer.OrdinalIgnoreCase)
    { "other", "unknown", "unspecified", "none", "misc", "default", "general", "standard", "normal" };

    /// <summary>Builds the distinctive entity-subtype VALUE→TABLE index. Reuses the catalog's bounded
    /// inline-enum probe, keeps only values from columns whose name ends with a configured subtype suffix
    /// (e.g. "Type"), drops short/multi-word/generic tokens, and keeps a value ONLY if exactly one table owns
    /// it (distinctive). Pure data, no business vocab. Fail-soft: any error yields an empty index.</summary>
    internal static IReadOnlyDictionary<string, (string Table, string Column)> BuildEnumValueIndex(
        IEntityCatalog catalog, IReadOnlyList<string> subtypeSuffixes, ILogger logger)
    {
        var empty = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (subtypeSuffixes is null || subtypeSuffixes.Count == 0) return empty;
        try
        {
            // value_lower -> (distinct owning tables, first table, first column)
            var owners = new Dictionary<string, (HashSet<string> Tables, string Table, string Column)>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in catalog.AllTables())
            {
                foreach (var (col, val) in catalog.GetInlineEnumValues(t.Name))
                {
                    if (!subtypeSuffixes.Any(s => !string.IsNullOrEmpty(s) && col.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                        continue;                                                   // entity-subtype columns only
                    if (string.IsNullOrWhiteSpace(val) || val.Length < 4) continue; // length floor (collision-prone short tokens)
                    if (val.IndexOf(' ') >= 0) continue;                            // single-token values only (a noun, not a phrase)
                    if (GenericValueStopWords.Contains(val)) continue;              // generic non-entity word
                    var key = val.ToLowerInvariant();
                    if (owners.TryGetValue(key, out var e)) e.Tables.Add(t.Name);
                    else owners[key] = (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { t.Name }, t.Name, col);
                }
            }
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, e) in owners)
                if (e.Tables.Count == 1) map[k] = (e.Table, e.Column);             // distinctive: exactly one owner
            logger.LogInformation("[SchemaLinker] enum-value index built: {Indexed} distinctive entity-subtype values (of {Seen} seen) over suffixes [{Suf}]",
                map.Count, owners.Count, string.Join(",", subtypeSuffixes));
            return map;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SchemaLinker] enum-value index build failed — value-anchoring disabled for this process (non-fatal)");
            return empty;
        }
    }

    /// <summary>Look up a question token in the value index, trying a single plural→singular 's' strip
    /// (mirrors IsAnchor) so "transformers" matches the value "Transformer". Returns the owning table or null.</summary>
    private (string Table, string Column)? LookupEnumValue(string token)
    {
        var idx = _enumValueIndex.Value;
        if (idx.TryGetValue(token, out var v)) return v;
        if (token.Length > 4 && token.EndsWith("s", StringComparison.Ordinal)
            && idx.TryGetValue(token[..^1], out var v2)) return v2;
        return null;
    }

    private IReadOnlyList<Regex> BuildNaturalKeyFormats()
    {
        var list = new List<Regex>();
        foreach (var e in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrWhiteSpace(e.NaturalKeyFormat)) continue;
            try { list.Add(new Regex(e.NaturalKeyFormat!, RegexOptions.IgnoreCase | RegexOptions.Compiled)); }
            catch (ArgumentException) { /* malformed format in config — skip, never crash a request */ }
        }
        return list;
    }

    public bool HasInScopeSignal(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var ql = " " + question.ToLowerInvariant() + " ";
        var tokens = Tokenize(question);
        foreach (var t in _knowledge.AllTables)
            if (IsAnchor(t, ql, tokens)) return true;

        var formats = _naturalKeyFormats.Value;
        if (formats.Count > 0)
            foreach (var raw in question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var tok = raw.Trim('.', ',', ';', ':', '?', '!', '(', ')', '\'', '"');
                if (tok.Length < 3) continue;
                foreach (var rx in formats)
                    if (rx.IsMatch(tok)) return true;
            }
        return false;
    }

    public async Task<IReadOnlyList<string>> LinkAsync(string question, CancellationToken cancellationToken = default)
    {
        var ql = " " + (question ?? string.Empty).ToLowerInvariant() + " ";
        var tokens = Tokenize(question);

        // Anchors = lexical name/synonym/camelCase-tail match + fuzzy-trigram match. High precision —
        // a table the question NAMES is almost certainly wanted; never evicted.
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _knowledge.AllTables)
            if (IsAnchor(t, ql, tokens)) anchors.Add(t.Name);

        var selected = new HashSet<string>(anchors, StringComparer.OrdinalIgnoreCase);

        // VALUE-INDEX anchor: a token that IS a distinctive entity-subtype value ("transformers" →
        // Assets.AssetType) names the entity-via-its-type and anchors NO table by name — so without this the
        // embedding fallback grabs the nearest name-matched table (Customers/Technicians) and the right table
        // is never retrieved (the entity-type→wrong-table confident-wrong). ADD the owning table. Additive
        // only: never evicts a lexical anchor, never calls the embedder; runs before the embedding fallback so
        // a value hit suppresses a wrong embedding pick. Restricted to entity-subtype columns + distinctive
        // values, so attribute words ("resolved"/"high") never steer selection.
        var lexicalAnchorCount = anchors.Count;
        var valueAnchored = false;
        if (_options.Value.EnableEnumValueAnchoring && _enumValueIndex.Value.Count > 0)
        {
            foreach (var token in tokens)
            {
                if (LookupEnumValue(token) is not { } hit) continue;
                if (!_catalog.TableExists(hit.Table)) continue;
                if (selected.Add(hit.Table)) { anchors.Add(hit.Table); valueAnchored = true; }
                _logger.LogDebug("[SchemaLinker] value-anchor token='{Tok}' → {Tbl}.{Col}", token, hit.Table, hit.Column);
            }
        }

        // Embedding is the RECALL fallback — only when the question names no table (e.g. "who handles
        // tickets", "staff"), so a 7B isn't handed a noisy slice for an already-anchored question.
        // linkMode records HOW the slice was chosen so a weak/desperate slice is diagnosable (ENH-5):
        //   anchor → lexical/synonym/trigram hit · embedding → above-floor cosine · best-effort →
        //   nothing cleared the floor (lowest confidence — the likeliest source of a wrong slice).
        var linkMode = valueAnchored ? (lexicalAnchorCount > 0 ? "anchor+value" : "value") : "anchor";
        if (selected.Count == 0)
        {
            var retrieval = await _retriever.RetrieveAsync(question, _options.Value.RetrieverTopK, cancellationToken);
            foreach (var m in retrieval.Tables.Where(m => m.Score >= EmbedFallbackFloor).Take(4))
                selected.Add(m.Table.Name);
            linkMode = "embedding";
            if (selected.Count == 0)
            {
                foreach (var m in retrieval.Tables.Take(3)) selected.Add(m.Table.Name);   // best effort
                linkMode = "best-effort";
                _logger.LogWarning(
                    "[SchemaLinker] q='{Q}' — no anchor and nothing cleared the {Floor} embedding floor; "
                    + "using a BEST-EFFORT slice (low confidence — a wrong slice is most likely here).",
                    question, EmbedFallbackFloor);
            }
        }

        if (selected.Count == 0) return System.Array.Empty<string>();

        var result = FkClosure(selected);
        _logger.LogInformation("[SchemaLinker] q='{Q}' mode={Mode} anchors=[{Anchors}] → slice=[{Slice}]",
            question, linkMode, string.Join(", ", anchors), string.Join(", ", result));
        return result;
    }

    private bool IsAnchor(InferredTable t, string ql, IReadOnlyList<string> tokens)
    {
        var name = t.Name.ToLowerInvariant();
        if (ql.Contains(name)) return true;
        if (name.Length > 3 && name.EndsWith("s") && ql.Contains(name[..^1])) return true;

        var tail = LastSegment(t.Name).ToLowerInvariant();
        if (tail.Length > 2 && tail != name &&
            (ql.Contains(" " + tail + " ") || ql.Contains(" " + tail + "s ")
             || (tail.EndsWith("s") && ql.Contains(" " + tail[..^1] + " "))))
            return true;

        if (t.Synonyms is { Count: > 0 })
            foreach (var syn in t.Synonyms)
                if (SynonymMatches(ql, syn)) return true;

        // Fuzzy: a query token within typo distance of the table's tail or a synonym.
        foreach (var token in tokens)
        {
            if (TrigramSimilarity.Compute(token, tail) >= FuzzyAnchorFloor) return true;
            if (t.Synonyms is { Count: > 0 })
                foreach (var syn in t.Synonyms)
                    if (TrigramSimilarity.Compute(token, syn) >= FuzzyAnchorFloor) return true;
        }
        return false;
    }

    /// <summary>Matched-set FK closure: add the small lookup tables a matched table references
    /// (Tickets.StatusId → TicketStatuses) plus bridge tables that connect TWO matched tables
    /// (AspNetUserRoles → AspNetUsers + AspNetRoles). Tight — no hub-radius dump.</summary>
    private List<string> FkClosure(HashSet<string> matched)
    {
        var result = new List<string>(matched);
        var have = new HashSet<string>(matched, StringComparer.OrdinalIgnoreCase);
        var fks = _catalog.Snapshot.ForeignKeys;

        foreach (var s in matched)
            foreach (var fk in fks)
                if (string.Equals(fk.ParentTable, s, StringComparison.OrdinalIgnoreCase))
                {
                    var target = fk.ReferencedTable;
                    if (have.Contains(target) || !_catalog.TableExists(target)) continue;
                    var t = _knowledge.GetTable(target);
                    if (t is not null && (t.Flags.IsLookup || t.Columns.Count <= 8) && have.Add(target))
                        result.Add(target);
                }

        foreach (var grp in fks.GroupBy(f => f.ParentTable, StringComparer.OrdinalIgnoreCase))
        {
            if (have.Contains(grp.Key) || !_catalog.TableExists(grp.Key)) continue;
            var refsMatched = grp.Select(f => f.ReferencedTable)
                                 .Where(rt => matched.Contains(rt))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (refsMatched >= 2 && have.Add(grp.Key)) result.Add(grp.Key);
        }

        // FIX-3 — transitive bridging: the loops above only add lookups + bridges that DIRECTLY
        // reference two matched tables, and the ≤8-column gate drops wide non-lookup tables. When two
        // anchored tables are joinable only via a multi-hop path through such a table (e.g.
        // Payments → Bills → Customers → Regions), materialize the intermediate tables on the shortest
        // FK path so the join is expressible. Bounded by MaxBridgePathHops to keep the slice tight.
        if (_options.Value.EnableTransitiveJoinBridging && matched.Count >= 2)
        {
            var matchedList = matched.ToList();
            for (int i = 0; i < matchedList.Count; i++)
                for (int j = i + 1; j < matchedList.Count; j++)
                {
                    var path = _fkGraph.FindPath(matchedList[i], matchedList[j]);
                    if (path is null || path.Count == 0 || path.Count > MaxBridgePathHops) continue;
                    foreach (var edge in path)
                        foreach (var node in new[] { edge.SourceTable, edge.TargetTable })
                            if (!matched.Contains(node) && _catalog.TableExists(node) && have.Add(node))
                                result.Add(node);
                }
        }
        return result;
    }

    // Synonym lexical match. LONG synonyms (>3 chars) use substring so an Arabic prefixed form like
    // "الفاتورة" still matches the synonym "فاتورة". SHORT synonyms (≤3 chars) require a WHOLE-TOKEN
    // match: a 2–3 char substring collides inside unrelated words (e.g. "حي" inside "صحي"=sanitation),
    // and because a spurious SOLE anchor suppresses the embedding-recall fallback that misroutes the
    // whole question. Curated config avoids short synonyms today; this makes any future one safe.
    private static bool SynonymMatches(string ql, string? syn)
    {
        if (string.IsNullOrWhiteSpace(syn)) return false;
        var s = syn.ToLowerInvariant();
        return s.Length > 3 ? ql.Contains(s) : ContainsWholeToken(ql, s);
    }

    private static bool ContainsWholeToken(string haystack, string needle)
    {
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            var leftOk = i == 0 || !char.IsLetter(haystack[i - 1]);
            var end = i + needle.Length;
            var rightOk = end >= haystack.Length || !char.IsLetter(haystack[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;
    }

    private static List<string> Tokenize(string? question) =>
        (question ?? string.Empty)
            .Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!', ';', ':', '(', ')', '\'', '"' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

    private static string LastSegment(string name)
    {
        int start = 0;
        for (int i = 1; i < name.Length; i++)
            if (char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                start = i;
        return name[start..];
    }
}
