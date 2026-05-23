namespace ServiceOpsAI.Models.AI
{
    public static class CopilotPlanVocabulary
    {
        public static class PromptTokens
        {
            public const string DataCatalog = "DATA_CATALOG";
            public const string ConversationContext = "CONVERSATION_CONTEXT";
            public const string KnownValues = "KNOWN_VALUES";
            public const string Question = "QUESTION";
        }

        public static class Operations
        {
            public const string Aggregate = "aggregate";
            public const string Breakdown = "breakdown";
            public const string Count = "count";
            public const string Detail = "detail";
            public const string List = "list";
        }

        public static class OutputShapes
        {
            public const string Metric = "metric";
            public const string Table = "table";
        }

        public static class Aggregations
        {
            public const string Avg = "avg";
            public const string Count = "count";
            public const string Sum = "sum";
        }

        public static class Operators
        {
            public const string Between = "between";
            public const string Equal = "equals";
            public const string GreaterThan = "gt";
            public const string IsNull = "isnull";
            public const string LessThan = "lt";
            public const string LessThanOrEqual = "lte";
            public const string NotEquals = "notequals";
        }

        public static class FieldNames
        {
            public const string AssignedToName = "AssignedToName";
            public const string AssignedToUserId = "AssignedToUserId";
            public const string AttachmentCount = "AttachmentCount";
            public const string AverageLatencyMs = "AverageLatencyMs";
            public const string AvgResolutionHours = "AvgResolutionHours";
            public const string CategoryName = "CategoryName";
            public const string CommentCount = "CommentCount";
            public const string CreatedByName = "CreatedByName";
            public const string CreatedByUserId = "CreatedByUserId";
            public const string DueDate = "DueDate";
            public const string EntityName = "EntityName";
            public const string HoursToResolution = "HoursToResolution";
            public const string Id = "Id";
            public const string IsActive = "IsActive";
            public const string IsClosedStatus = "IsClosedStatus";
            public const string IsEnabled = "IsEnabled";
            public const string IsSlaBreached = "IsSlaBreached";
            public const string Name = "Name";
            public const string PriorityName = "PriorityName";
            public const string ResolvedAt = "ResolvedAt";
            public const string ResolvedByName = "ResolvedByName";
            public const string ResolvedByUserId = "ResolvedByUserId";
            public const string ResolvedTickets = "ResolvedTickets";
            public const string ResolutionHours = "ResolutionHours";
            public const string SlaBreaches = "SlaBreaches";
            public const string SlaBreachRate = "SlaBreachRate";
            public const string SourceName = "SourceName";
            public const string StatusName = "StatusName";
            public const string SuccessCount = "SuccessCount";
            public const string SuccessRate = "SuccessRate";
            public const string TicketNumber = "TicketNumber";
            public const string Title = "Title";
            public const string TotalElapsedMs = "TotalElapsedMs";
            public const string TotalTickets = "TotalTickets";
            public const string UpdatedAt = "UpdatedAt";
        }

        public static class EntityNames
        {
            public const string Entity = "Entity";
            public const string TicketCategory = "TicketCategory";
            public const string TicketPriority = "TicketPriority";
            public const string TicketSource = "TicketSource";
            public const string TicketStatus = "TicketStatus";
            public const string User = "User";
        }

        public static class RelationshipNames
        {
            public const string Assignee = "Assignee";
            public const string Reporter = "Reporter";
            public const string ResolvedBy = "ResolvedBy";
        }

        public static class FieldTypes
        {
            public const string DateTime = "datetime";
        }

        public static class Capabilities
        {
            public const string Group = "group";
        }

        public static class Aliases
        {
            public const string TotalCount = "TotalCount";
        }

        public static class SemanticRoles
        {
            public const string ConfidenceScore = "confidence-score";
            public const string AttachmentCount = "attachment-count";
            public const string CommentCount = "comment-count";
            public const string ResolutionDuration = "resolution-duration";
            public const string SlaBreach = "sla-breach";
            public const string SlaBreachRate = "sla-breach-rate";
            public const string Identifier = "identifier";
            public const string TicketReference = "ticket-reference";
            public const string ExternalReference = "external-reference";
            public const string CaseAlias = "case-alias";
        }

        public static class ValueTypes
        {
            public const string Field = "Field";
        }

        public static class LookupValuePrefixes
        {
            public const string EntityOf = "entity of ";
            public const string MinistryOf = "ministry of ";
        }

        public static class JsonProperties
        {
            public const string Direction = "direction";
        }

        public static class RegexGroups
        {
            public const string Count = "count";
            public const string Days = "days";
            public const string Hours = "hours";
            public const string Limit = "limit";
        }

        public static class ValidationMessages
        {
            public const string MissingGroupBy = "requested grouping or breakdown but plan has no group-by field";
            public const string MissingFilter = "requested filters or a specific record but plan has no filter";
            public const string UnsupportedOrTextFilter = "requested OR text matching that is not yet expressible by the safe catalog plan";
            public const string AnswerShapeGatePrefix = "AnswerShapeGate: ";
            public const string PlanMismatchPrefix = "I cannot produce a reliable data answer yet because the validated plan does not match the requested shape: ";

            public static string RequestedEvidenceMismatch(string expectedEntity, string actualEntity)
                => $"requested {expectedEntity} evidence but plan targeted {actualEntity}";

            public static string RequestedLimitMismatch(int requestedLimit, int? actualLimit)
                => $"requested limit {requestedLimit} but plan limit is {actualLimit}";

            public static string FieldNotFound(string fieldRef)
                => $"Field '{fieldRef}' not found.";

            public static string UnsupportedAggregation(string function, string target)
                => $"requested {function} aggregation but no approved catalog metric supports it for {target}";
        }

        public static class PlannerMessages
        {
            public const string DeterministicExplanation = "Deterministic core data planner matched common analytics wording.";
            public const string FallbackClarification = "I could not map this request to an approved catalog entity. Please ask for a specific data area such as tickets, users, roles, comments, history, Copilot traces, assessments, or tools.";
            public const string FallbackExplanation = "Fallback catalog mapping selected the closest approved entity.";
            public const string ModelOutputNotJsonObject = "Model planner output was not a JSON object.";
            public const string ModelPlanningFailed = "Elite analytical planning failed because model output did not satisfy the CopilotDataIntentPlan contract.";
        }

        public static class ExecutionMessages
        {
            public const string UngroundedAnswer = "I do not have retrieved Copilot evidence for that question, so I cannot answer it as confirmed. Please ask it against an approved catalog entity, source table, or concrete record reference.";
            public const string UngroundedSummary = "Blocked ungrounded Copilot answer because no approved data, record, knowledge-base, or related-record evidence was retrieved.";
            public const string UngroundedGeneralAction = "Ungrounded General Response Gate [CopilotExecutionEngine -> ExecuteGeneralChatAsync]";
            public const string UngroundedGeneralDetail = "Skipped model-only general response because the question requires retrieved evidence.";
            public const string UngroundedInvestigationAction = "Ungrounded Investigation Response Gate [CopilotInvestigationExecutor -> ExecuteAsync]";
            public const string UngroundedInvestigationDetail = "Skipped evidence synthesis because the investigation retrieved no record, knowledge-base, or related-record evidence.";
        }

        public static class RegexPatterns
        {
            public const string QuotedTextToken = "['\"]";
            public const string UnsupportedOrTextFilter = @"['""][^'""]+['""]\s+or\s+['""][^'""]+['""]";

            public static string ExactNormalizedPhrase(string normalizedPhrase)
                => $@"(?<!\S){normalizedPhrase}(?!\S)";
        }

        public static class Syntax
        {
            public const string FieldSeparator = ".";
            public const string SentenceTerminator = ".";
            public const string SemicolonSpace = "; ";
            public const string Underscore = "_";
        }

        public static class SortDirectionTokens
        {
            public const string Asc = "asc";
            public const string Ascending = "ascending";
            public const string Desc = "desc";
            public const string Descending = "descending";
            public const string NormalizedAsc = "Asc";
            public const string NormalizedDesc = "Desc";
        }
    }
}
