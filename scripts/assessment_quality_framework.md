# Copilot Assessment Quality Framework

## What Defines a "Good Answer"?

Based on the `CopilotAssessmentResult.IsSuccess` logic at lines 274-290, a good answer must pass ALL of these checks:

### 1. Primary Checks (All Required)
- **No Exception** - No error thrown during processing
- **Mode Match** - Copilot used expected mode (DynamicTicketQuery, ExternalUtility, GeneralSupport)
- **Intent Match** - Detected intent matches expected (DataQuery, ExternalToolQuery, GeneralChat, etc.)
- **Tool Match** - Correct tool selected (if tool expected)
- **Context Match** - Record context used correctly when required
- **Results Count** - Returns expected number of results (if specified)
- **Answer Quality** - Passes keyword validation and contains usable results
- **Clarification Handling** - Correctly requests or avoids clarification as expected
- **Invalid Handling** - Correctly rejects invalid questions when expected
- **Decomposition** - Creates expected number of workflows for complex queries
- **Latency** - Completes within 30000ms (30 seconds)

### 2. Query Plan Checks (For Data Queries)
- **Entity Match** - Queries correct primary entity/table
- **Operation Match** - Uses correct operation (Count, List, Breakdown, etc.)
- **Fields Match** - Selects expected fields/columns
- **GroupBy Match** - Groups by expected fields
- **Filters Match** - Applies expected filter criteria
- **Aggregations Match** - Performs expected aggregations

### 3. Answer Quality Validation (lines 510-560)
- **No Empty Answers** - Answer must contain content
- **No Failure Keywords** - Cannot contain "couldn't retrieve", "external tool failed"
- **No Empty Data Responses** - "Found N records" without actual data is a fail
- **Expected Keywords Present** - Must contain all `ExpectedAnswerKeywords`
- **Forbidden Keywords Absent** - Must NOT contain any `ForbiddenAnswerKeywords`

## Common Failure Patterns (from AssessmentDetail)

| Pattern | Meaning | Example |
|---------|---------|---------|
| "entity expected X, got Y" | Wrong table queried | Asked for entities, queried tickets |
| "intent expected X, got Y" | Misclassification | DataQuery expected, got GeneralChat |
| "mode expected X, got Y" | Wrong processing path | Dynamic query expected, got general support |
| "tool expected X, got Y" | Wrong tool selected | Expected KB search, got SQL query |
| "answer did not contain..." | Missing expected keywords | Asked for count, no number in answer |
| "expected clarification request" | Should ask for clarification but didn't | Ambiguous question got direct (wrong) answer |
| "unexpected clarification request" | Asked for clarification when shouldn't | Clear question got "please clarify" |
| "expected N result(s), got M" | Wrong result count | Expected 5 tickets, got 0 or 50 |
| "latency exceeded 30000ms" | Timeout | Complex query took too long |
| "record context missing" | Failed to use ticket context | Follow-up question lost ticket reference |

## What to Look for in Sessions 1, 0, 3, 7

### Session-by-Session Analysis Questions:

1. **Success Rate Trends**
   - Is Session 7 (latest) better than Session 0 (earliest)?
   - Are we improving or regressing?

2. **Intent Detection Accuracy**
   - Which intents are most often misclassified?
   - Is DataQuery being confused with GeneralChat?

3. **Entity Recognition**
   - When asking about "entities", does it query Entitys table or default to Tickets?
   - When asking about "users", does it query AspNetUsers correctly?

4. **Complex Query Handling**
   - "How many tickets per entity" - does it perform GROUP BY correctly?
   - "Average tickets per entity" - does it handle AVG aggregation?
   - Multi-part questions - does it decompose correctly?

5. **Clarification Behavior**
   - Is it asking for clarification when it should answer directly?
   - Is it answering directly when it should ask for clarification?

6. **Performance**
   - Are queries completing within 30 seconds?
   - Is the Agentic copilot faster or slower than previous versions?

## Assessment of "Good Answers"

A "good answer" is one where:
1. The copilot understood the question correctly (intent + entity)
2. The copilot generated the correct query/plan
3. The copilot returned accurate data
4. The copilot presented the answer clearly
5. The copilot handled edge cases appropriately (clarification/rejection)

**Red Flags for Bad Answers:**
- Empty or generic responses
- Wrong entity queried (tickets instead of entities)
- Missing aggregations (asked for average, got list)
- Clarification loops (keeps asking "please clarify" on clear questions)
- Hallucinated data (returns numbers not from database)
- Timeout failures
