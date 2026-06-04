namespace AnalystAgent.Tests.Eval;

using System.Collections.Generic;
using ServiceOpsAI.Models.AI;
using Xunit;

/// <summary>
/// Phase-0 accuracy oracle: execution accuracy is the real, deterministic, latency-separated grader.
/// The headline guarantees: (1) a DataQuery case with NO gold can no longer pass-by-default; (2) gold
/// present + EX null (gold threw) is a FAIL, not a silent pass; (3) latency is NOT part of correctness;
/// (4) a known-wrong query (rows differ) fails. These pin the metric that the whole methodology rides on.
/// </summary>
public class OracleGradingTests
{
    private static AdminCopilotStructuredResultRow Row(params string[] cells)
    {
        var r = new AdminCopilotStructuredResultRow();
        for (int i = 0; i < cells.Length; i++) r.Values[$"c{i}"] = cells[i];
        return r;
    }

    private static CopilotChatResponse DataResponse(string intent = "DataQuery", params AdminCopilotStructuredResultRow[] rows)
    {
        var resp = new CopilotChatResponse { Answer = "Returned results." };
        resp.ExecutionDetails.DetectedIntent = intent;
        resp.StructuredRows.AddRange(rows);
        return resp;
    }

    private static CopilotChatResponse Resp(string intent, string? notes, params AdminCopilotStructuredResultRow[] rows)
    {
        var resp = new CopilotChatResponse { Answer = "Returned results.", Notes = notes };
        resp.ExecutionDetails.DetectedIntent = intent;
        resp.StructuredRows.AddRange(rows);
        return resp;
    }

    private static AdminCopilotStructuredResultRow RowKv(params (string k, string v)[] cells)
    {
        var r = new AdminCopilotStructuredResultRow();
        foreach (var (k, v) in cells) r.Values[k] = v;
        return r;
    }

    private static CopilotAssessmentResult Result(CopilotAssessmentCase c, CopilotChatResponse? resp,
        bool? exAccuracy = null, long latencyMs = 1000)
        => new() { Case = c, ActualResponse = resp, ExAccuracy = exAccuracy, LatencyMs = latencyMs };

    [Fact]
    public void PassedExAccuracy_NullWithGold_Fails()   // the regression that used to PASS
    {
        var r = Result(new CopilotAssessmentCase { ExpectedSql = "SELECT COUNT(*) FROM Bills" }, null, exAccuracy: null);
        Assert.False(r.PassedExAccuracy);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void PassedExAccuracy_NullNoGold_Passes()
    {
        var r = Result(new CopilotAssessmentCase { ExpectedInvalid = true }, null, exAccuracy: null);
        Assert.True(r.PassedExAccuracy);   // nothing to execute → not a failure of EX
    }

    [Fact]
    public void PassedExAccuracy_RowsDiffer_Fails()
    {
        var r = Result(new CopilotAssessmentCase { ExpectedSql = "SELECT 1" }, DataResponse(), exAccuracy: false);
        Assert.False(r.PassedExAccuracy);
    }

    [Fact]
    public void DataQueryCase_NoGold_IsNotCorrectByDefault()   // abstain/no-gold no longer free pass
    {
        var c = new CopilotAssessmentCase { ExpectedIntent = CopilotIntentKind.DataQuery };  // no gold
        var r = Result(c, DataResponse("DataQuery"), exAccuracy: null);
        Assert.True(r.IsDataQueryCase);
        Assert.False(r.HasGold);
        Assert.False(r.IsSuccess);   // (!IsDataQueryCase || HasGold) gate fails it
    }

    [Fact]
    public void Latency_IsNotPartOfCorrectness()
    {
        var c = new CopilotAssessmentCase { ExpectedSql = "SELECT 1", ExpectedIntent = CopilotIntentKind.DataQuery };
        var r = Result(c, DataResponse("DataQuery", Row("1")), exAccuracy: true,
                       latencyMs: CopilotAssessmentResult.DefaultMaxLatencyMs + 999999);
        Assert.True(r.IsSuccess);              // correct...
        Assert.False(r.WithinLatencyBudget);   // ...but slow — reported separately, not a fail
    }

    [Fact]
    public void KnownWrongQuery_Fails_WhereItUsedToPass()
    {
        // gold for 'Paid' bills; copilot answered (rows present) but EX says result sets differ.
        var c = new CopilotAssessmentCase { ExpectedSql = "SELECT COUNT(*) FROM Bills WHERE Status='Paid'", ExpectedIntent = CopilotIntentKind.DataQuery };
        var r = Result(c, DataResponse("DataQuery", Row("999")), exAccuracy: false);
        Assert.False(r.IsSuccess);
    }

    [Theory]
    [InlineData("13", true)]
    [InlineData("7", false)]
    [InlineData("not-a-number", false)]
    public void PassedScalar_GradesFirstCellValue(string cell, bool expected)
    {
        var c = new CopilotAssessmentCase { ExpectedScalar = 13m };
        var r = Result(c, DataResponse("DataQuery", Row(cell)));
        Assert.Equal(expected, r.PassedScalar);
    }

    [Fact]
    public void PassedRowCount_GradesResultSetSize_NotValue()
    {
        var c = new CopilotAssessmentCase { ExpectedRowCount = 3 };
        Assert.True(Result(c, DataResponse("DataQuery", Row("a"), Row("b"), Row("c"))).PassedRowCount);
        Assert.False(Result(c, DataResponse("DataQuery", Row("a"), Row("b"))).PassedRowCount);
    }

    // ── Skeptic-found holes: an abstain / 0-row result must NOT pass a data-query case ──────────

    [Fact]
    public void DataQuery_ZeroRows_WithOnlyMaxBound_Fails()   // issue B: ExpectedMaxRows-only no longer hides an abstain
    {
        var c = new CopilotAssessmentCase { ExpectedMaxRows = 1, ExpectedIntent = CopilotIntentKind.DataQuery };
        var r = Result(c, Resp("DataQuery", "SELECT COUNT(*) FROM x"));   // ran SQL, but 0 rows
        Assert.True(r.IsDataQueryCase);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void DataQuery_ZeroRows_WhenGoldExpectsEmpty_Passes()   // legitimate empty result
    {
        var c = new CopilotAssessmentCase { ExpectedRowCount = 0, ExpectedIntent = CopilotIntentKind.DataQuery };
        var r = Result(c, Resp("DataQuery", "SELECT * FROM Tickets WHERE 1=0"));   // ran SQL, expected-empty
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void SqlContainsOnlyGold_Abstain_Fails()   // issue C: SQL-text gold now classifies as data + is gated
    {
        var c = new CopilotAssessmentCase { ExpectedSqlContains = new() { "SELECT" }, ExpectedIntent = CopilotIntentKind.DataQuery };
        var abstain = Resp("DataQuery", notes: null);   // no SQL, no rows
        var r = Result(c, abstain);
        Assert.True(r.IsDataQueryCase);
        Assert.True(r.HasGold);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void PassedScalar_MatchesAnyCell_NotJustFirst()   // issue F: leftover label column before the aggregate
    {
        var c = new CopilotAssessmentCase { ExpectedScalar = 13m };
        var r = Result(c, Resp("DataQuery", "SELECT 'x' AS Label, COUNT(*) AS N", RowKv(("Label", "x"), ("N", "13"))));
        Assert.True(r.PassedScalar);
    }

    [Fact]
    public void Scalar_And_RowCount_AreSeparateAxes()
    {
        // A scalar=13 case must NOT be satisfied by 13 rows; a rowcount=13 case must NOT be satisfied by a cell of 13.
        var scalarCase = new CopilotAssessmentCase { ExpectedScalar = 13m };
        Assert.False(Result(scalarCase, DataResponse("DataQuery", Row("x"))).PassedScalar);   // cell 'x' != 13
        var countCase = new CopilotAssessmentCase { ExpectedRowCount = 13 };
        Assert.False(Result(countCase, DataResponse("DataQuery", Row("13"))).PassedRowCount); // 1 row != 13
    }
}
