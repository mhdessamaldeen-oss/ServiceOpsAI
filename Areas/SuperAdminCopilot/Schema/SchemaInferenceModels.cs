namespace SuperAdminCopilot.Schema;

// ── Schema inference output (serialized to schema-inferred.json) ───────────────────────

public sealed record SchemaInferenceProgress(int TablesTotal, int TablesDone, string? CurrentTable);

public sealed class SchemaInferenceResult
{
    public string Version { get; init; } = "2.0";
    public DateTimeOffset GeneratedAt { get; init; }
    public string SchemaHash { get; init; } = string.Empty;
    public List<InferredTable> Tables { get; init; } = new();
}

public sealed class InferredTable
{
    public required string Name { get; init; }
    public required string Schema { get; init; }
    public string? Description { get; set; }
    public List<string> PrimaryKey { get; set; } = new();
    public InferredFlags Flags { get; set; } = new();
    public InferredRoles Roles { get; set; } = new();
    public List<InferredColumn> Columns { get; set; } = new();
    public List<ForeignKeyRef> ForeignKeysOut { get; set; } = new();
    public List<ReferencedByRef> ReferencedBy { get; set; } = new();
    /// <summary>Up to 10 representative values from the label column. Populated for lookup tables
    /// (statuses, priorities, categories) so the LLM doesn't have to guess valid filter values —
    /// it sees "Open / Closed / In Progress" and uses one of those literally.</summary>
    public List<string> SampleValues { get; set; } = new();
    public string Source { get; set; } = "heuristic";
}

public sealed class InferredFlags
{
    public bool IsBridge { get; set; }
    public bool IsLookup { get; set; }
    public bool IsPerson { get; set; }
}

public sealed class InferredRoles
{
    public string? LabelColumn { get; set; }
    public string? SoftDeleteColumn { get; set; }
    public string? NaturalKey { get; set; }
}

public sealed class InferredColumn
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Nullable { get; set; }
    public string? Role { get; set; }
    public string? DateRole { get; set; }
    /// <summary>Verb-role for FK columns ("creator" / "assignee" / "resolver" / …). Only set when
    /// <see cref="Role"/> is <c>foreign_key</c>. Lets the LLM pick the right FK when the question
    /// uses a verb like "created" or "assigned". See <see cref="Models.SpecConstants.FkRoles"/>.</summary>
    public string? FkRole { get; set; }
    public string? References { get; set; }
    public bool IsPii { get; set; }

    // Phase 4 annotations — operator-curated per-column metadata that the planner prompt
    // and SchemaLinker ranker consume. All optional; null/empty means "no annotation".
    // Description: one-line natural-language meaning of the column ("total spend in cents",
    // "ISO 4217 currency code"). Flows into the planner prompt so the LLM doesn't have to
    // guess what a column means from its name.
    public string? Description { get; set; }
    // Synonyms: alternate phrasings users say for this column ("revenue" ↔ "sales" ↔
    // "amount"). Drives the SchemaLinker's cosine match + helps the planner pick the
    // right column when the question uses one of these instead of the canonical name.
    public List<string>? Synonyms { get; set; }
    // SampleValues: representative values for filter columns (top 5–10). Lets the LLM
    // emit a literal that the data actually contains instead of a hallucinated value.
    public List<string>? SampleValues { get; set; }
}

public sealed record ForeignKeyRef(string Column, string Table, string ReferencedColumn);

public sealed record ReferencedByRef(string FromTable, string FromColumn);

// ── Human-curated overrides (loaded from schema-overrides.json) ────────────────────────

public sealed class SchemaOverrides
{
    public List<TableOverride> Tables { get; set; } = new();
}

public sealed class TableOverride
{
    public required string Name { get; init; }
    public string? Description { get; set; }
    public InferredFlagsOverride? Flags { get; set; }
    public InferredRolesOverride? Roles { get; set; }
    public List<ColumnOverride>? Columns { get; set; }

    public bool HasAnyValue() =>
        Description is not null || Flags is not null || Roles is not null || (Columns?.Count > 0);
}

public sealed class InferredFlagsOverride
{
    public bool? IsBridge { get; set; }
    public bool? IsLookup { get; set; }
    public bool? IsPerson { get; set; }
}

public sealed class InferredRolesOverride
{
    public string? LabelColumn { get; set; }
    public string? SoftDeleteColumn { get; set; }
    public string? NaturalKey { get; set; }
}

public sealed class ColumnOverride
{
    public required string Name { get; init; }
    public string? Role { get; set; }
    public string? DateRole { get; set; }
    public string? FkRole { get; set; }
    public string? References { get; set; }
    public bool? IsPii { get; set; }
    // Phase 4 — human-curated description / synonyms / sample values applied on top of the
    // auto-inferred metadata. Null = leave inferred value untouched; non-null = replace.
    public string? Description { get; set; }
    public List<string>? Synonyms { get; set; }
    public List<string>? SampleValues { get; set; }
}

// ── Inference job state (singleton runtime state exposed to the admin UI) ──────────────

public enum SchemaInferenceJobStatus { Idle, Running, Completed, Failed }

public sealed class SchemaInferenceJobState
{
    public SchemaInferenceJobStatus Status { get; init; } = SchemaInferenceJobStatus.Idle;
    public int TablesTotal { get; init; }
    public int TablesDone { get; init; }
    public string? CurrentTable { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
    public bool FileExists { get; init; }
    public DateTimeOffset? FileLastGeneratedAt { get; init; }
    public int? FileTableCount { get; init; }
    public string? FilePath { get; init; }
    public int PercentComplete => TablesTotal > 0 ? (int)(100.0 * TablesDone / TablesTotal) : 0;
}
