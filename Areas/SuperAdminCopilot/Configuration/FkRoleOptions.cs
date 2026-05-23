namespace SuperAdminCopilot.Configuration;

/// <summary>
/// Configuration for FK verb-role inference. Maps column-name patterns to roles ("creator",
/// "assignee", "resolver", …) and provides the question verbs the LLM should map to each role.
/// <para>One source of truth: <see cref="Schema.SchemaInferenceGenerator"/> uses
/// <see cref="FkRolePattern.Patterns"/> to infer FK roles when generating <c>schema-inferred.json</c>;
/// <see cref="Pipeline.Stages.SpecExtractor"/> uses <see cref="FkRolePattern.QuestionVerbs"/> to
/// build the prompt rule that teaches the LLM how to map a question's verb to the right FK.</para>
/// <para>Hot-reloaded via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>:
/// operators can add a role or a new naming convention by editing the JSON; no recompile.
/// Schema inference must be re-run after the JSON changes to bake the new roles into the
/// inferred file — same model as adding a new PII pattern or label-column rule.</para>
/// </summary>
public sealed class FkRoleOptions
{
    public const string SectionName = "FkRoles";

    /// <summary>One entry per role. Order matters: the first matching pattern wins, so
    /// more-specific entries should appear before more-general ones (e.g. "AssigneeId" before
    /// "Owner" since AssignedToOwnerId would otherwise match Owner first).</summary>
    public List<FkRolePattern> Patterns { get; set; } = new();
}

/// <summary>One verb-role rule. <see cref="Role"/> is the canonical role label written into the
/// inferred schema and the prompt; <see cref="Patterns"/> is the list of name patterns
/// (case-insensitive) that identify a column as having this role; <see cref="QuestionVerbs"/>
/// is the list of natural-language verb phrases the LLM should map to this role.</summary>
public sealed class FkRolePattern
{
    /// <summary>Canonical role label (lower-case, single word recommended). Surfaces in the
    /// inferred schema's <c>FkRole</c> field and in the SpecExtractor prompt rule.</summary>
    public string Role { get; set; } = "";

    /// <summary>Column-name match patterns. Each pattern is a case-insensitive substring match
    /// (the column-name suffix conventions vary by schema; <c>CreatedBy</c> matches both
    /// <c>CreatedByUserId</c> [PascalCase] and <c>created_by_user_id</c> [snake_case] because
    /// matching strips underscores before comparison).</summary>
    public List<string> Patterns { get; set; } = new();

    /// <summary>Natural-language verb phrases that should resolve to this role. Used by the
    /// SpecExtractor prompt to teach the LLM how to map question verbs to FK columns.</summary>
    public List<string> QuestionVerbs { get; set; } = new();
}
