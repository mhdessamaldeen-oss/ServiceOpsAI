namespace AnalystAgent.Eval.Paraphrase;

/// <summary>
/// Final report from a paraphrase-robustness run. The key signal is
/// <see cref="WorstPerturbationDropPct"/> — Dr.Spider's headline metric (the worst per-perturbation
/// drop relative to the base phrasing). A robust system has a small drop across all perturbations;
/// a fragile system has one or two perturbations that crater. The report is plain data so it can
/// be serialised to JSON for nightly dashboards, written to disk, or compared run-over-run.
/// </summary>
public sealed record ParaphraseRobustnessReport
{
    public DateTime RunStartedUtc { get; init; }
    public DateTime RunCompletedUtc { get; init; }
    public string SuiteName { get; init; } = "";

    /// <summary>Total paraphrase scenarios attempted.</summary>
    public int TotalScenarios { get; init; }

    /// <summary>Number of distinct <see cref="ClusterResult.ClusterId"/> buckets observed
    /// in the scenario set.</summary>
    public int TotalClusters { get; init; }

    /// <summary>Aggregate pass rate across every scenario regardless of perturbation.</summary>
    public double OverallPassRate { get; init; }

    /// <summary>
    /// Dr.Spider headline metric — the largest accuracy drop on any single perturbation
    /// category relative to the base phrasing. Smaller = more robust. A score &gt; 30pp
    /// indicates a category the pipeline cannot handle and should be the next fix target.
    /// </summary>
    public double WorstPerturbationDropPct { get; init; }

    /// <summary>Name of the perturbation category that produced
    /// <see cref="WorstPerturbationDropPct"/>.</summary>
    public string WorstPerturbation { get; init; } = "";

    /// <summary>Per-perturbation breakdown — the actionable view.</summary>
    public IReadOnlyList<PerturbationReport> ByPerturbation { get; init; } = Array.Empty<PerturbationReport>();

    /// <summary>Per-cluster breakdown — useful when diagnosing which intent broke.</summary>
    public IReadOnlyList<ClusterResult> ByCluster { get; init; } = Array.Empty<ClusterResult>();
}

/// <summary>Aggregated accuracy for one perturbation category.</summary>
public sealed record PerturbationReport
{
    public string Perturbation { get; init; } = "";
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Errored { get; init; }
    public double PassRate { get; init; }
}

/// <summary>Per-cluster outcome. Lets a reviewer drill from "bracket-square dropped 30%"
/// to "which 3 clusters caused most of that drop".</summary>
public sealed record ClusterResult
{
    public string ClusterId { get; init; } = "";
    public string Category { get; init; } = "";
    public int Total { get; init; }
    public int Passed { get; init; }
    public double PassRate { get; init; }
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = Array.Empty<ScenarioResult>();
}

/// <summary>One executed scenario.</summary>
public sealed record ScenarioResult
{
    public string Code { get; init; } = "";
    public string Question { get; init; } = "";
    public string Perturbation { get; init; } = "";
    public string ClusterId { get; init; } = "";
    public bool Passed { get; init; }
    public string? FailureReason { get; init; }
    public long LatencyMs { get; init; }
    public int ExpectedRowCount { get; init; }
    public int ActualRowCount { get; init; }
    public string? GeneratedSql { get; init; }
}
