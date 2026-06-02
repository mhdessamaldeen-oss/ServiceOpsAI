namespace SuperAdminCopilot.Compilation;

using System.Linq;
using System.Text.RegularExpressions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;
using SpecConst = SuperAdminCopilot.Models.SpecConstants;

/// <summary>
/// Pre-emission rewrites for <see cref="FilterSpec"/> values. Owns three concerns that
/// previously lived inline in <c>SqlCompiler.Where.cs</c>:
/// <list type="bullet">
///   <item><b>String-literal column-ref drop (#6)</b>: when the filter value is a literal
///         <c>"Table.Column"</c> that resolves to a real column, drop the filter — the
///         relationship is already wired by the join graph.</item>
///   <item><b>Lookup-name rewrite (#5)</b>: <c>StatusId='Closed'</c> →
///         <c>TicketStatuses.Name='Closed'</c> when the FK + label column exist.</item>
///   <item><b>Value-synonym rewrite</b>: <c>"urgent"</c> → <c>"Critical"</c> via
///         <see cref="ISemanticLayer.ResolveSynonymValue"/>.</item>
/// </list>
///
/// <para>Extracted from <c>SqlCompiler.Where.cs</c> as part of the 2026-06-01 de-couple pass.
/// Filter-value rewriting is logically independent from WHERE-clause building; moving it out
/// makes the WHERE-builder smaller, makes the rewrite logic individually testable, and
/// prevents future filter-value features (regex normalization, currency-symbol stripping,
/// etc.) from getting tangled with operator emission.</para>
/// </summary>
public interface IFilterValueRewriter
{
    /// <summary>Apply all rewrites in the canonical order. Returns the original spec when
    /// nothing matches. Never mutates the input.</summary>
    FilterSpec Rewrite(FilterSpec filter);
}

internal sealed class FilterValueRewriter : IFilterValueRewriter
{
    private static readonly Regex TableColumnLiteralPattern = new(
        @"^[A-Za-z_]\w*\.[A-Za-z_]\w*$", RegexOptions.Compiled);

    private readonly IEntityCatalog _catalog;
    private readonly ISemanticLayer _semanticLayer;

    public FilterValueRewriter(IEntityCatalog catalog, ISemanticLayer semanticLayer)
    {
        _catalog = catalog;
        _semanticLayer = semanticLayer;
    }

    public FilterSpec Rewrite(FilterSpec filter)
    {
        if (filter is null) return filter!;

        // #6 — string-literal column reference: drop via placeholder when value is "Table.Column"
        // and both halves resolve. The relationship is already expressed by the join graph.
        if (filter.Value is string svRef
            && TableColumnLiteralPattern.IsMatch(svRef))
        {
            var (refTable, refCol) = SplitQualified(svRef);
            if (!string.IsNullOrEmpty(refTable) && !string.IsNullOrEmpty(refCol)
                && _catalog.ColumnExists(refTable, refCol))
            {
                return new FilterSpec
                {
                    Column = filter.Column,
                    Op = SpecConst.FilterOps.Eq,
                    Value = "@drop_columnref",
                };
            }
        }

        // #5 — lookup-name filter rewrite when value is a non-@ string. Skip pure numerics
        // (user filtered by ID directly).
        if (filter.Value is string svLookup && svLookup.Length > 0 && svLookup[0] != '@')
        {
            var rewritten = TryRewriteLookupNameFilter(filter, svLookup);
            if (rewritten is not null) return ApplySynonymRewrite(rewritten);
        }

        return ApplySynonymRewrite(filter);
    }

    private FilterSpec ApplySynonymRewrite(FilterSpec filter)
    {
        if (filter.Value is not string sv || sv.Length == 0 || sv[0] == '@') return filter;
        var canonical = _semanticLayer.ResolveSynonymValue(filter.Column, sv);
        if (ReferenceEquals(canonical, sv) || string.Equals(canonical, sv, System.StringComparison.Ordinal))
            return filter;
        return new FilterSpec { Column = filter.Column, Op = filter.Op, Value = canonical };
    }

    private FilterSpec? TryRewriteLookupNameFilter(FilterSpec filter, string stringValue)
    {
        var (table, col) = SplitQualified(filter.Column);
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(col)) return null;
        if (!col.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase)) return null;
        if (!_catalog.TableExists(table)) return null;
        if (long.TryParse(stringValue, out _)) return null;

        var fk = _catalog.Snapshot.ForeignKeys.FirstOrDefault(f =>
            string.Equals(f.ParentTable, table, System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.ParentColumn, col, System.StringComparison.OrdinalIgnoreCase));
        if (fk is null) return null;
        if (!_catalog.TableExists(fk.ReferencedTable)) return null;

        // Prefer the entity's explicit LabelColumn; otherwise walk the semantic-layer
        // Defaults.LabelColumnPreference fallback list (all data-driven, no hardcoded names).
        string? labelCol = null;
        var refEntity = _semanticLayer.GetEntityForTable(fk.ReferencedTable);
        if (refEntity is not null && !string.IsNullOrEmpty(refEntity.LabelColumn)
            && _catalog.ColumnExists(fk.ReferencedTable, refEntity.LabelColumn))
        {
            labelCol = refEntity.LabelColumn;
        }
        if (labelCol is null)
        {
            foreach (var candidate in _semanticLayer.Config.Defaults.LabelColumnPreference)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (_catalog.ColumnExists(fk.ReferencedTable, candidate)) { labelCol = candidate; break; }
            }
        }
        if (labelCol is null) return null;

        return new FilterSpec
        {
            Column = $"{fk.ReferencedTable}.{labelCol}",
            Op = filter.Op,
            Value = stringValue,
        };
    }

    private static (string Table, string Column) SplitQualified(string s)
    {
        if (string.IsNullOrEmpty(s)) return ("", "");
        var dot = s.IndexOf('.');
        if (dot <= 0) return ("", s);
        return (s.Substring(0, dot), s.Substring(dot + 1));
    }
}
