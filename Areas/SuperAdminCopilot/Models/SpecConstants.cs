namespace SuperAdminCopilot.Models;

/// <summary>
/// Canonical constants for every string the QuerySpec protocol uses. The LLM emits these
/// literals; the compiler / validator / normalizer compare against them. Anywhere a literal
/// would appear in code, reference one of these instead so a typo or rename is caught at
/// compile time.
/// </summary>
public static class SpecConstants
{
    // ── QuerySpec.Intent ─────────────────────────────────────────────────
    public static class Intent
    {
        public const string DataQuery     = "data_query";
        public const string Clarification = "clarification";
        public const string Greeting      = "greeting";
        public const string Knowledge     = "knowledge";
        public const string Tool          = "tool";
        public const string Metadata      = "metadata";
        public const string SemanticSearch = "semantic_search";
        public const string Refuse        = "refuse";
    }

    // ── FilterSpec.Op ────────────────────────────────────────────────────
    public static class FilterOps
    {
        public const string Eq         = "eq";
        public const string Neq        = "neq";
        public const string Gt         = "gt";
        public const string Gte        = "gte";
        public const string Lt         = "lt";
        public const string Lte        = "lte";
        public const string Like       = "like";
        public const string NotLike    = "not_like";
        public const string In         = "in";
        public const string NotIn      = "not_in";
        public const string NotInAlt   = "notin";        // alternate spelling the LLM sometimes emits
        public const string IsNull     = "is_null";
        public const string IsNullAlt  = "isnull";
        public const string NotNull    = "not_null";
        public const string NotNullAlt = "notnull";
        // Magic op: compiler expands into a cross-column OR LIKE over the entity's SearchableColumns.
        public const string TextSearch = "text_search";
    }

    // ── AggregateSpec.Function ──────────────────────────────────────────
    public static class Aggregates
    {
        public const string Count = "COUNT";
        public const string Sum   = "SUM";
        public const string Avg   = "AVG";
        public const string Min   = "MIN";
        public const string Max   = "MAX";
        public const string Star  = "*";   // COUNT(*) convention
    }

    // ── JoinSpec.Kind ────────────────────────────────────────────────────
    public static class JoinKinds
    {
        public const string Inner = "inner";
        public const string Left  = "left";
        public const string Anti  = "anti";
    }

    // ── OrderBySpec.Direction ───────────────────────────────────────────
    public static class SortDirections
    {
        public const string Asc  = "asc";
        public const string Desc = "desc";
    }

    // ── HavingSpec.Op (mirrors FilterOps but reserved for HAVING) ──────
    // (Same op vocabulary as FilterOps; this nested class is for readability at the call site.)
    public static class HavingOps
    {
        public const string Eq  = FilterOps.Eq;
        public const string Neq = FilterOps.Neq;
        public const string Gt  = FilterOps.Gt;
        public const string Gte = FilterOps.Gte;
        public const string Lt  = FilterOps.Lt;
        public const string Lte = FilterOps.Lte;
    }

    // ── InferredColumn.Role (semantic role within the table) ───────────
    public static class ColumnRoles
    {
        public const string PrimaryKey  = "primary_key";
        public const string ForeignKey  = "foreign_key";
        public const string NaturalKey  = "natural_key";
        public const string Label       = "label";
        public const string SoftDelete  = "soft_delete";
        public const string Date        = "date";
        public const string Audit       = "audit";
    }

    // ── InferredColumn.FkRole (when Role = "foreign_key") ──────────────
    // Verb-role of a foreign-key column, inferred from the column-name suffix.
    // Lets the LLM disambiguate "tickets CREATED by X" vs "tickets ASSIGNED to X"
    // without enumerating every entity. Department-agnostic — applies to any table
    // whose FK columns follow the conventional *By / *To naming.
    public static class FkRoles
    {
        public const string Creator   = "creator";   // CreatedBy*, OpenedBy*, RaisedBy*
        public const string Modifier  = "modifier";  // ModifiedBy*, UpdatedBy*, LastModifiedBy*, EditedBy*
        public const string Assignee  = "assignee";  // AssignedTo*, AssigneeId, HandledBy*
        public const string Owner     = "owner";     // OwnerId, OwnerUserId
        public const string Resolver  = "resolver";  // ResolvedBy*, ClosedBy*
        public const string Approver  = "approver";  // ApprovedBy*, AuthorizedBy*
        public const string Reviewer  = "reviewer";  // ReviewedBy*
        public const string Deleter   = "deleter";   // DeletedBy*
        public const string Submitter = "submitter"; // SubmittedBy*
    }

    // ── InferredColumn.DateRole (when Role = "date") ───────────────────
    public static class DateRoles
    {
        // Core lifecycle (pre-Phase 06)
        public const string Created   = "created";
        public const string Modified  = "modified";
        public const string Deleted   = "deleted";
        public const string Completed = "completed";
        public const string Resolved  = "resolved";
        public const string Started   = "started";
        public const string Due       = "due";

        // Phase 06 utility-domain vocabulary (Activated, Issued, Paid, …) — added so the
        // date-role detector recognises bill / payment / contract / field-ops lifecycle
        // columns without per-table config.
        public const string Activated    = "activated";    // ServiceAccount.ActivatedAt
        public const string Deactivated  = "deactivated";  // ServiceAccount.DeactivatedAt
        public const string Issued       = "issued";       // Bill.IssuedAt, Subsidy.IssuedAt
        public const string Paid         = "paid";         // Payment.PaidAt, Bill.PaidAt
        public const string Hired        = "hired";        // Technician.HiredAt
        public const string Installed    = "installed";    // ServicePoint.InstalledAt
        public const string Dispatched   = "dispatched";   // WorkOrder.DispatchedAt
        public const string Arrived      = "arrived";      // WorkOrder.ArrivedOnSiteAt
        public const string Sent         = "sent";         // OutageNotification.SentAt
        public const string Delivered    = "delivered";    // OutageNotification.DeliveredAt
        public const string Read         = "read";         // OutageNotification.ReadAt
        public const string Commissioned = "commissioned"; // Asset.CommissionedAt
        public const string Decommissioned = "decommissioned"; // Asset.DecommissionedAt
        public const string Effective    = "effective";    // Tariff.EffectiveFrom / EffectiveTo
        public const string Scheduled    = "scheduled";    // MaintenanceSchedule.ScheduledStart
        public const string Signup       = "signup";       // Customer.SignupAt
        public const string Churned      = "churned";      // Customer.ChurnedAt
        public const string Responded    = "responded";    // CsatResponse.RespondedAt, Ticket.FirstRespondedAt
        public const string Escalated    = "escalated";    // Ticket.EscalatedAt
        public const string Approved     = "approved";     // ResolutionApprovedAt
    }

    // ── InferredTable.Source ─────────────────────────────────────────────
    public static class InferenceSources
    {
        public const string Heuristic        = "heuristic";
        public const string HeuristicPlusOverride = "heuristic+override";
        public const string Ai               = "ai-generated";
        public const string Manual           = "manual";
    }
}
