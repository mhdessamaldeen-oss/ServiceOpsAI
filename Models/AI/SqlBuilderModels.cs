namespace ServiceOpsAI.Models.AI
{
    /// <summary>
    /// Filter condition for WHERE clause construction
    /// </summary>
    public class FilterCriteria
    {
        public string Field { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty; // equals, contains, gt, lt, between, in, isnull
        public object? Value { get; set; }
        public bool IsNegated { get; set; }
    }
    
    /// <summary>
    /// SQL clause types for transparent query construction
    /// </summary>
    public enum SqlClauseType
    {
        Select,      // SELECT / TOP / DISTINCT / Projection fields
        From,        // FROM [Table] AS [Alias]
        Join,        // LEFT JOIN [Table] AS [Alias] ON [Condition]
        Where,       // WHERE [Conditions]
        GroupBy,     // GROUP BY [Fields]
        Having,      // HAVING [Aggregate Conditions]
        OrderBy,     // ORDER BY [Fields] [ASC/DESC]
        OffsetFetch  // OFFSET [N] ROWS FETCH NEXT [M] ROWS ONLY
    }
}
