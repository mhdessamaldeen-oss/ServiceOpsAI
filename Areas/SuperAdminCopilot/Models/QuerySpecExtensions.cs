namespace SuperAdminCopilot.Models;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Ergonomic helpers for mutable <see cref="QuerySpec"/>. Each method mutates the spec in place
/// and returns it for chaining. The single-spec architecture (2026-06-01 collapse) eliminated
/// the v2/v3 split — all repair rules and the bus speak this mutable spec directly. No more
/// converter, no more immutable round-trip.
///
/// <para>The previous immutable-style "with"-based helpers in <c>Domain/Spec/QuerySpecExtensions.cs</c>
/// were the v3 form of the same API. Rule call-sites are unchanged because the method names
/// map 1:1.</para>
/// </summary>
public static class QuerySpecExtensions
{
    public static QuerySpec WithRoot(this QuerySpec spec, string root)
    {
        spec.Root = root;
        return spec;
    }

    public static QuerySpec AddFilter(this QuerySpec spec, FilterSpec filter)
    {
        spec.Filters.Add(filter);
        return spec;
    }

    public static QuerySpec AddFilters(this QuerySpec spec, params FilterSpec[] filters)
    {
        spec.Filters.AddRange(filters);
        return spec;
    }

    public static QuerySpec WithFilters(this QuerySpec spec, IEnumerable<FilterSpec> filters)
    {
        spec.Filters = filters.ToList();
        return spec;
    }

    public static QuerySpec AddSelect(this QuerySpec spec, string column)
    {
        spec.Select.Add(column);
        return spec;
    }

    public static QuerySpec WithSelect(this QuerySpec spec, IEnumerable<string> select)
    {
        spec.Select = select.ToList();
        return spec;
    }

    public static QuerySpec AddJoin(this QuerySpec spec, JoinSpec join)
    {
        spec.Joins.Add(join);
        return spec;
    }

    public static QuerySpec AddAggregation(this QuerySpec spec, AggregateSpec agg)
    {
        spec.Aggregations.Add(agg);
        return spec;
    }

    public static QuerySpec WithAggregations(this QuerySpec spec, IEnumerable<AggregateSpec> aggs)
    {
        spec.Aggregations = aggs.ToList();
        return spec;
    }

    public static QuerySpec WithLimit(this QuerySpec spec, int? limit)
    {
        spec.Limit = limit;
        return spec;
    }

    public static QuerySpec WithDistinct(this QuerySpec spec, bool distinct)
    {
        spec.Distinct = distinct;
        return spec;
    }
}
