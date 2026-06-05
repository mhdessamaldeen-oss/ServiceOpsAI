namespace AnalystAgent.Grounding;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Configuration;
using AnalystAgent.Pipeline.EntityResolution;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// Default <see cref="IValueLinker"/>: scans question text for whole-word matches against the
/// actual values of FK-reachable lookup tables. Uses <see cref="IEntityCatalog.GetAllLookupValues"/>
/// to enumerate values (cached + capped at 500 per table).
///
/// <para>Search expands beyond the retriever's top-K: we also walk 1- and 2-hop FK neighbors of
/// every linked table, so a question about Tickets in Damascus finds Regions even if Regions
/// wasn't in the top-K retrieval result. This mirrors how CHESS / X-SQL expand the link set
/// before SQL generation.</para>
/// </summary>
internal sealed class ValueLinker : IValueLinker
{
    private readonly IEntityCatalog _catalog;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IFuzzyEntityResolver _fuzzyResolver;
    private readonly Abstractions.ITextEmbedder _embedder;
    private readonly IOptions<AnalystOptions> _options;
    private readonly Schema.IAnalystSchemaAccessPolicy _accessPolicy;
    private readonly ILogger<ValueLinker> _logger;

    // Process-wide cache of (table.col=value -> embedding) for cross-lingual matching. Values are stable
    // (the data's enum domain), so this is computed once and reused across questions.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> _valueEmbedCache = new();

    public ValueLinker(
        IEntityCatalog catalog,
        ISemanticLayer semanticLayer,
        IFuzzyEntityResolver fuzzyResolver,
        Abstractions.ITextEmbedder embedder,
        IOptions<AnalystOptions> options,
        Schema.IAnalystSchemaAccessPolicy accessPolicy,
        ILogger<ValueLinker> logger)
    {
        _catalog = catalog;
        _semanticLayer = semanticLayer;
        _fuzzyResolver = fuzzyResolver;
        _embedder = embedder;
        _options = options;
        _accessPolicy = accessPolicy;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ValueLinkBinding>> LinkAsync(
        string question,
        IReadOnlyList<InferredTable> linkedTables,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question) || linkedTables.Count == 0)
            return System.Array.Empty<ValueLinkBinding>();

        // Build the whole-word search corpus from the question.
        var qLow = " " + question.ToLowerInvariant() + " ";
        var sb = new System.Text.StringBuilder(qLow.Length);
        foreach (var ch in qLow)
        {
            if (ch == ',' || ch == '.' || ch == '!' || ch == '?' || ch == ';' || ch == ':' || ch == '"' || ch == '\'')
                sb.Append(' ');
            else sb.Append(ch);
        }
        qLow = sb.ToString();

        // Expand the link set: each linked table + 1- and 2-hop FK neighbors that are lookup-shaped.
        // ACCESS GATE: never probe a table that isn't QUERYABLE — the copilot's own operational tables
        // (Copilot*), EF/identity-internal, and any RetrieverHidden table must never become a value source.
        // This closes the self-poisoning hole where the question text matched its own logged chat-session Title.
        var lookupCandidates = ExpandToLookupNeighbors(linkedTables.Select(t => t.Name).ToList())
            .Where(t => _accessPolicy.IsTableQueryable(t))
            .ToList();

        var results = new List<ValueLinkBinding>();
        var seenPerTableColumn = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var table in lookupCandidates)
        {
            var values = _catalog.GetAllLookupValues(table);
            if (values.Count == 0) continue;
            foreach (var (col, val) in values)
            {
                // Length >= 3: a 2-char lookup value ("Q1", "L1") whole-word-matches far too easily and was a
                // documented source of spurious filters (a quarter token binding to a status value). Whole-word
                // + >=3 chars keeps real values ("Open", "Paid", "Damascus") while dropping incidental shorts.
                if (val.Length < 3) continue;
                var needle = " " + val.ToLowerInvariant() + " ";
                if (!qLow.Contains(needle, System.StringComparison.Ordinal)) continue;

                // De-dup at (table, column, VALUE) level — so two distinct values in the SAME column
                // (e.g. "tickets from Damascus AND Aleppo", both in Regions.NameEn) BOTH bind, while an
                // exact duplicate is still suppressed. Keying on (table,column) alone silently dropped
                // every value after the first. Values are bounded (≤500/table) so this stays cheap.
                var key = table + "." + col + "=" + val;
                if (!seenPerTableColumn.Add(key)) continue;

                results.Add(new ValueLinkBinding(
                    Table: table,
                    Column: col,
                    Value: val,
                    MatchedToken: val,
                    Confidence: 1.0f));
            }
        }

        // Inline-enum pass: bind question tokens to LOW-CARDINALITY string-column VALUES on the linked
        // tables THEMSELVES (Bills.Status='Overdue', Outages.Severity='Critical') — fact-table enums the
        // lookup pass above never sees (it only enumerates small lookup TABLES, bailing over 500 rows).
        // Same whole-word, >=3-char discipline. The binding flows through BuildGroundingHints + the
        // deterministic injector, so "overdue bills" gets WHERE Bills.Status='Overdue' enforced instead of
        // the model's invented date predicate. Schema-driven (cardinality-gated), portable — no value list.
        foreach (var t in linkedTables)
        {
            if (!_accessPolicy.IsTableQueryable(t.Name)) continue;   // never inline-bind on a hidden/operational table
            foreach (var (col, val) in _catalog.GetInlineEnumValues(t.Name))
            {
                if (val.Length < 3) continue;
                // Whole-word match, PLURAL-AWARE: an entity-subtype value ("Transformer") is named in the
                // plural ("how many transformerS") — match the value's regular plural too so the filter binds.
                // Additive: status values ("Overdue"/"Critical") aren't pluralized in questions, so no over-bind.
                if (!QuestionContainsValueWord(qLow, val.ToLowerInvariant())) continue;
                // Skip when the enum word is used as a VERB (immediately followed by a preposition) rather
                // than an ADJECTIVE modifying the entity: "bills ISSUED in the last 30 days" / "bills PAID
                // by cash" are date/method filters, NOT a Status filter — while "overdue BILLS",
                // "active ACCOUNTS", "completed work ORDERS" are real status filters (followed by a noun).
                // Fact-table enum values double as common past-tense verbs (issued/paid/completed), so this
                // cuts the over-bind the fresh TEMPORAL suite exposed without touching the adjective bindings.
                if (IsVerbContext(qLow, val.ToLowerInvariant())) continue;
                var key = t.Name + "." + col + "=" + val;
                if (!seenPerTableColumn.Add(key)) continue;
                results.Add(new ValueLinkBinding(
                    Table: t.Name, Column: col, Value: val, MatchedToken: val, Confidence: 1.0f));
            }
        }

        // Fuzzy fallback (ADD-1): catch typo'd lookup values the exact whole-word pass missed
        // ("Dmascus" → "Damascus"). Scoped to the SAME FK-reachable lookup candidates so a fuzzy
        // value from an unrelated table never binds, and the match similarity becomes the binding
        // confidence (distinguishing it from an exact 1.0 bind). No-op when the index is empty.
        if (_options.Value.EnableFuzzyValueLinking)
        {
            var candidateSet = new HashSet<string>(lookupCandidates, System.StringComparer.OrdinalIgnoreCase);
            var fuzzy = await _fuzzyResolver.ResolveAsync(question, cancellationToken);
            foreach (var hit in fuzzy)
            {
                if (hit.Canonical.Length < 2 || !candidateSet.Contains(hit.Table)) continue;
                var key = hit.Table + "." + hit.Column + "=" + hit.Canonical;
                if (!seenPerTableColumn.Add(key)) continue;   // exact pass already bound this value
                results.Add(new ValueLinkBinding(
                    Table: hit.Table,
                    Column: hit.Column,
                    Value: hit.Canonical,
                    MatchedToken: hit.Surface,
                    Confidence: (float)hit.Similarity));
            }
        }

        // Cross-lingual value pass: an ARABIC question ("الفواتير المتأخرة") can't whole-word/trigram match
        // the ENGLISH data value ("Overdue"), so every value-filtered Arabic question returned all rows.
        // bge-m3 is multilingual — embed the Arabic content words + the linked tables' enum/lookup VALUES
        // and bind the nearest. NO hand-curated synonym table; the embedder bridges the languages. Gated to
        // NON-English questions, so it can never affect English linking (cost + spurious-bind safety).
        if (!string.Equals(Internal.QuestionLanguageDetector.Detect(question),
                Internal.QuestionLanguageDetector.English, System.StringComparison.OrdinalIgnoreCase))
        {
            await AddCrossLingualValueLinksAsync(question, lookupCandidates, linkedTables, results, seenPerTableColumn, cancellationToken);
        }

        if (results.Count > 0)
        {
            _logger.LogInformation("[ValueLinker] resolved {Count} value link(s): {Pairs}",
                results.Count,
                string.Join(", ", results.Select(r => $"{r.Table}.{r.Column}='{r.Value}'")));
        }

        return results;
    }

    /// <summary>
    /// Returns the union of: (a) <paramref name="seeds"/> themselves and (b) all tables reachable
    /// via 1-hop FK from any seed AND whose entity is marked IsLookup OR has a Label column
    /// (heuristic lookup-shaped). The expansion is bidirectional (parent ↔ referenced).
    /// <para>1 hop, NOT 2: a 2-hop bidirectional reach pulled in DISTANT, unrelated lookups — e.g. an
    /// "outages" question reached TicketPriorities via Regions ← Tickets → TicketPriorities, so "critical"
    /// mis-bound TicketPriorities.Name instead of Outages.Severity and forced an invalid join. A direct
    /// (1-hop) FK neighbor is the lookup the question's tables actually reference.</para>
    /// </summary>
    // Prepositions that, immediately AFTER an enum word, mark it as a verb/temporal usage rather than an
    // adjective filter ("issued IN ...", "paid BY ...", "created ON ..."). Not 'of'/'to'/'at' (too weak).
    private static readonly HashSet<string> VerbContextPrepositions = new(System.StringComparer.OrdinalIgnoreCase)
    { "in", "by", "on", "during", "over", "since", "between", "within", "before", "after", "from" };

    /// <summary>Whole-word match of an enum value against the (space-padded, lowercased) question, PLURAL-aware:
    /// matches the value itself ("transformer") OR its regular plural ("transformers") so an entity-subtype
    /// value named in the plural still binds its filter. The plural form is only TRIED, never required, so
    /// non-pluralizable status values ("overdue"/"critical") are unaffected. Mirrors the schema linker's
    /// single-'s' morphology (no stemming) to stay consistent and avoid over-matching.</summary>
    private static bool QuestionContainsValueWord(string qLowPadded, string valLower)
    {
        if (qLowPadded.Contains(" " + valLower + " ", System.StringComparison.Ordinal)) return true;
        if (valLower.Length >= 4 && !valLower.EndsWith("s", System.StringComparison.Ordinal)
            && qLowPadded.Contains(" " + valLower + "s ", System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the enum <paramref name="valLower"/> appears immediately followed by a
    /// verb-context preposition in the (space-padded, lowercased) question — i.e. it's the verb in
    /// "bills <b>issued in</b> the last 30 days", not the adjective in "overdue bills". Used only by the
    /// inline-enum pass, whose values double as common past-tense verbs.</summary>
    private static bool IsVerbContext(string qLowPadded, string valLower)
    {
        var needle = " " + valLower + " ";
        var idx = qLowPadded.IndexOf(needle, System.StringComparison.Ordinal);
        if (idx < 0) return false;
        var rest = qLowPadded.Substring(idx + needle.Length);
        var words = rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && VerbContextPrepositions.Contains(words[0]);
    }

    /// <summary>Bind Arabic question words to English enum VALUES via the multilingual embedder. Candidates
    /// are restricted to LOW-CARDINALITY enums (status/severity/type), not large entity-name lists (those are
    /// already bilingual via NameAr and would be slow to embed). Per content word, bind its single nearest
    /// value above a cosine threshold + margin. Process-cached value vectors; question-scoped word vectors.</summary>
    private async System.Threading.Tasks.Task AddCrossLingualValueLinksAsync(
        string question, List<string> lookupCandidates, IReadOnlyList<InferredTable> linkedTables,
        List<ValueLinkBinding> results, HashSet<string> seen, CancellationToken ct)
    {
        const int maxEnumValuesPerColumn = 30;   // skip big entity-name lists; keep status/type enums
        var candidates = new List<(string Table, string Col, string Val)>();
        foreach (var table in lookupCandidates)
        {
            var vals = _catalog.GetAllLookupValues(table);
            if (vals.Count == 0 || vals.Count > maxEnumValuesPerColumn) continue;   // big list -> not a status enum
            foreach (var (col, val) in vals) if (val.Length >= 3) candidates.Add((table, col, val));
        }
        foreach (var t in linkedTables)
        {
            if (!_accessPolicy.IsTableQueryable(t.Name)) continue;   // never bind on a hidden/operational table
            foreach (var (col, val) in _catalog.GetInlineEnumValues(t.Name))
                if (val.Length >= 3) candidates.Add((t.Name, col, val));
        }
        if (candidates.Count == 0) return;

        var words = ExtractContentWords(question);
        if (words.Count == 0) return;

        var valVecs = new List<((string Table, string Col, string Val) C, float[] Vec)>();
        foreach (var c in candidates)
        {
            var key = c.Table + "." + c.Col + "=" + c.Val;
            if (seen.Contains(key)) continue;
            var vec = await EmbedValueCachedAsync(key, c.Val, ct);
            if (vec.Length > 0) valVecs.Add((c, vec));
        }
        if (valVecs.Count == 0) return;

        // Entity-vs-value anchor: embed the candidate TABLE NAMES. A word binds to a value ONLY if it is
        // more VALUE-like than ENTITY-like — "الفواتير" (bills) is closer to the Bills table than to any
        // value (skip), while "المتأخرة" (overdue) is closer to the value 'Overdue' than to "Bills" (bind).
        // This separates status/type descriptors from entity nouns without a fragile absolute threshold.
        var tableVecs = new List<float[]>();
        foreach (var tn in candidates.Select(c => c.Table).Distinct(System.StringComparer.OrdinalIgnoreCase))
        {
            var v = await EmbedValueCachedAsync("TABLE::" + tn, tn, ct);
            if (v.Length > 0) tableVecs.Add(v);
        }

        // Absolute cosine bar for a cross-lingual value match. Measured separation: valid status words
        // (overdue 0.67, paid 0.79, active 0.86, open 0.82) sit ABOVE spurious entity-noun matches
        // (bills->Wallet 0.64, regions->District 0.64). 0.66 keeps the real status filters and rejects the
        // entity nouns. (A genuinely ambiguous word like "critical"->0.49 is left unbound rather than guessed.)
        const float threshold = 0.66f;
        const float margin = 0.05f;
        const float entityMargin = 0.02f;   // value-cos must also beat the best table-name-cos (secondary guard)
        foreach (var w in words)
        {
            var wvec = await SafeEmbedAsync(w, ct);
            if (wvec.Length == 0) continue;
            float best = -1f, second = -1f; int bestIdx = -1;
            for (int i = 0; i < valVecs.Count; i++)
            {
                var cs = Cosine(wvec, valVecs[i].Vec);
                if (cs > best) { second = best; best = cs; bestIdx = i; }
                else if (cs > second) { second = cs; }
            }
            float bestTableCos = 0f;
            foreach (var tv in tableVecs) { var tc = Cosine(wvec, tv); if (tc > bestTableCos) bestTableCos = tc; }
            if (bestIdx >= 0 && best >= threshold && (best - second) >= margin && best > bestTableCos + entityMargin)
            {
                var c = valVecs[bestIdx].C;
                var key = c.Table + "." + c.Col + "=" + c.Val;
                if (seen.Add(key))
                {
                    results.Add(new ValueLinkBinding(Table: c.Table, Column: c.Col, Value: c.Val, MatchedToken: w, Confidence: best));
                    _logger.LogInformation("[ValueLinker] cross-lingual bind '{Word}' -> {Table}.{Col}='{Val}' (cos={Cos:F2})", w, c.Table, c.Col, c.Val, best);
                }
            }
        }
    }

    private async System.Threading.Tasks.Task<float[]> SafeEmbedAsync(string text, CancellationToken ct)
    {
        try { return await _embedder.EmbedAsync(text, ct) ?? System.Array.Empty<float>(); }
        catch (OperationCanceledException) { throw; }
        catch { return System.Array.Empty<float>(); }
    }

    private async System.Threading.Tasks.Task<float[]> EmbedValueCachedAsync(string key, string val, CancellationToken ct)
    {
        if (_valueEmbedCache.TryGetValue(key, out var cached)) return cached;
        var vec = await SafeEmbedAsync(val, ct);
        if (vec.Length > 0) _valueEmbedCache[key] = vec;
        return vec;
    }

    private static readonly HashSet<string> CrossLingualStopWords = new(System.StringComparer.OrdinalIgnoreCase)
    { "how","many","what","which","show","list","count","number","total","the","all","are","there","have","our","do","we","of","in","is",
      "كم","عدد","ما","هو","هي","كل","من","في","لدينا","اعرض","قائمة","جميع","هل","التي","عن","إجمالي","مجموع","يوجد","كانت" };

    /// <summary>Content words (>=3 chars, minus EN/AR function words) — and for Arabic words carrying the
    /// definite article "ال", also the stripped form ("المتأخرة" -> "متأخرة"), which embeds closer to the value.</summary>
    private static List<string> ExtractContentWords(string question)
    {
        var raw = question.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', '،', '؛', '؟', ':', ';', '"', '\'' },
            System.StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raww in raw)
        {
            var w = raww.Trim();
            if (w.Length < 3 || CrossLingualStopWords.Contains(w)) continue;
            if (seen.Add(w)) result.Add(w);
            if (w.Length > 4 && w.StartsWith("ال", System.StringComparison.Ordinal))
            {
                var stripped = w.Substring(2);
                if (stripped.Length >= 3 && seen.Add(stripped)) result.Add(stripped);
            }
        }
        return result;
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0f;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / (System.Math.Sqrt(na) * System.Math.Sqrt(nb)));
    }

    private List<string> ExpandToLookupNeighbors(List<string> seeds)
    {
        var visited = new HashSet<string>(seeds, System.StringComparer.OrdinalIgnoreCase);
        var lookupTables = new List<string>();
        var allFks = _catalog.Snapshot.ForeignKeys;

        // BFS, 1 hop only (see remarks — 2-hop reached unrelated lookups and caused mis-binds).
        var frontier = new List<(string Table, int Hops)>();
        foreach (var s in seeds) frontier.Add((s, 0));
        // Seeds themselves are also candidates if they're lookup-shaped.
        foreach (var s in seeds)
            if (IsLookupShaped(s)) lookupTables.Add(s);

        while (frontier.Count > 0)
        {
            var next = new List<(string, int)>();
            foreach (var (current, hops) in frontier)
            {
                if (hops >= 1) continue;
                foreach (var fk in allFks)
                {
                    string? neighbor = null;
                    if (string.Equals(fk.ParentTable, current, System.StringComparison.OrdinalIgnoreCase))
                        neighbor = fk.ReferencedTable;
                    else if (string.Equals(fk.ReferencedTable, current, System.StringComparison.OrdinalIgnoreCase))
                        neighbor = fk.ParentTable;
                    if (string.IsNullOrEmpty(neighbor)) continue;
                    if (!visited.Add(neighbor)) continue;
                    if (!_catalog.TableExists(neighbor)) continue;

                    if (IsLookupShaped(neighbor)) lookupTables.Add(neighbor);
                    next.Add((neighbor, hops + 1));
                }
            }
            frontier = next;
        }
        return lookupTables;
    }

    private bool IsLookupShaped(string tableName)
    {
        var entity = _semanticLayer.GetEntityForTable(tableName);
        if (entity?.IsLookup == true) return true;
        // Heuristic fallback: has a Label-style column AND GetAllLookupValues returns something
        // (the catalog caps at 500 rows, so Tickets/Bills/Outages return empty).
        var cols = _catalog.GetColumns(tableName);
        var hasLabel = cols.Any(c =>
            string.Equals(c.ColumnName, "Name", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "NameEn", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Title", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Code", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, "Label", System.StringComparison.OrdinalIgnoreCase));
        if (!hasLabel) return false;
        return _catalog.GetAllLookupValues(tableName).Count > 0;
    }
}
