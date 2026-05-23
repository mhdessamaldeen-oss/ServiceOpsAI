namespace SuperAdminCopilot.Configuration;

/// <summary>
/// Configuration for the deterministic write-intent preflight gate
/// (<see cref="Pipeline.Stages.WriteIntentGuard"/>). Verb patterns live in
/// <c>write-intent-verbs.json</c> (path: <c>WriteIntentVerbsPath</c> on
/// <see cref="CopilotOptions"/>) and are hot-reloaded via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
/// — operators add a new language or verb by editing the JSON; no recompile.
/// </summary>
public sealed class WriteIntentOptions
{
    public const string SectionName = "WriteIntent";

    /// <summary>One entry per supported language. Default file ships with English + Arabic; add
    /// more by appending entries to the JSON.</summary>
    public List<WriteIntentLanguage> Languages { get; set; } = new();
}

/// <summary>Patterns for one language. <see cref="Locale"/> is BCP-47 short (en / ar / fr / …)
/// — surfaced in the trace so the audit panel can show which language tripped the gate.</summary>
public sealed class WriteIntentLanguage
{
    /// <summary>BCP-47 short locale code: en / ar / fr / es / etc.</summary>
    public string Locale { get; set; } = "";

    /// <summary>Patterns to match against the user's question. Each pattern is a raw .NET regex
    /// — operators authoring the JSON are expected to understand regex. Word-boundary anchoring
    /// is the author's responsibility (English needs <c>\b…\b</c>; Arabic / CJK don't use Latin
    /// word boundaries). Compiled lazily on first use; recompiled on options change.</summary>
    public List<WriteIntentVerb> Verbs { get; set; } = new();
}

/// <summary>A single write-verb rule. <see cref="Pattern"/> is the regex; <see cref="Verb"/> is
/// the canonical English label shown in the refusal reason ("refused: delete (ar)").</summary>
public sealed class WriteIntentVerb
{
    /// <summary>Raw regex pattern (case-insensitive matching is applied automatically).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Canonical English label shown in the trace and refusal reason.</summary>
    public string Verb { get; set; } = "";
}
