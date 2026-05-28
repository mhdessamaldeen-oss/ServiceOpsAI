namespace SuperAdminCopilot.Pipeline.SpecRepair.Phases;

using System.Text.Json;
using SuperAdminCopilot.Models;

/// <summary>Strip wrapping single/double quotes from filter values ("'Overdue'" → "Overdue"). Handles JsonElement + string.</summary>
internal sealed class StripQuotedFilterValuesPhase : ISpecRepairPhase
{
    public string Name => "StripQuotedFilterValues";
    public string Covers => "LLM wraps string filter values in SQL-style quotes";

    public void Apply(QuerySpec spec, SpecRepairContext ctx)
    {
        int mutated = 0;
        var delimiters = ctx.Options.QuotedValueDelimiters;
        foreach (var f in spec.Filters)
        {
            var unquoted = Unquote(f.Value, delimiters);
            if (!ReferenceEquals(unquoted, f.Value)) { f.Value = unquoted; mutated++; }
        }
        foreach (var h in spec.Having)
        {
            var unquoted = Unquote(h.Value, delimiters);
            if (!ReferenceEquals(unquoted, h.Value)) { h.Value = unquoted; mutated++; }
        }
        if (mutated > 0) ctx.Diagnostics.Add(new(Name, $"unquoted {mutated} value(s)"));
    }

    private static object? Unquote(object? value, System.Collections.Generic.List<string> delimiters)
    {
        string? s = value switch
        {
            string vs => vs,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => null,
        };
        if (s is null || s.Length < 2) return value;
        foreach (var d in delimiters)
        {
            if (d.Length != 1) continue;
            if (s[0] == d[0] && s[s.Length - 1] == d[0])
                return s.Substring(1, s.Length - 2);
        }
        return value;
    }
}
