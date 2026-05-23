namespace SuperAdminCopilot.Pipeline;

using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;

// Workflow Graph — backend-authoritative data model for the Investigation > Workflow tab.
//
// Problem this solves: the Razor template + investigation-pipeline.js have ~1600 lines of
// hardcoded stage descriptions, route detection ("did data run? did clarification run?"),
// and graph layout. Every orchestrator change requires three coordinated edits: Stages.cs,
// the Razor route-detection block, and the JS STEP_DESCRIPTIONS dictionary. They drift.
// The UI breaks on every change.
//
// Solution: this model is the single source of truth. It carries everything the UI needs to
// render the diagram WITHOUT any per-stage logic of its own:
//   • What stages exist (label, description, section, branch column) — from PipelineArchitecture.
//   • Which stages actually fired this run (status, elapsed, kind, input/output/reason).
//   • Which transitions were taken (edges).
//   • Which sub-steps fired inside each stage.
// Frontend's only job: draw nodes + edges from this graph. No conditional zone logic, no
// hardcoded description strings, no route detection.

public sealed record WorkflowGraph(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    string Path,                 // "DataQuery" | "VerifiedQuery" | "ExternalTool" | "SemanticSearch" | "GeneralChat" | "Refusal" | "Error"
    string Outcome,              // "ok" | "failed" | "refused"
    long TotalElapsedMs,
    int LlmCallCount);

public sealed record WorkflowNode(
    string Id,                   // canonical name from PipelineArchitecture (e.g. "specextractor")
    string Label,                // display label ("Spec Extractor")
    string Description,          // one-paragraph explanation — from PipelineStageDescriptor
    string Section,              // "rail-pre" | "router-probe" | "decomposer" | "branch" | "rail-post" | "terminal"
    string? BranchColumn,        // "DataQuery" | "VerifiedQuery" | ... when Section == "branch"; null otherwise
    bool Mandatory,              // whether the stage MUST fire in every run
    string Status,               // "ok" | "failed" | "skipped" | "not-on-path" | "passthrough"
    bool OnPath,                 // was this node traversed by the run?
    bool Fired,                  // did it emit a recorded PipelineStep?
    long? ElapsedMs,
    string? Kind,                // "llm-call" | "sql-execution" | "tool-dispatch" | "branch" | "gate" | null
    string? Input,
    string? Output,
    string? Reason,
    string? DetailsJson,
    IReadOnlyList<WorkflowNode> SubSteps);

public sealed record WorkflowEdge(
    string FromId,
    string ToId,
    bool WasTaken,
    string? Condition);

public interface IWorkflowGraphBuilder
{
    WorkflowGraph Build(IReadOnlyList<PipelineStep> steps, string? error, long totalElapsedMs);
}

internal sealed class WorkflowGraphBuilder : IWorkflowGraphBuilder
{
    public WorkflowGraph Build(IReadOnlyList<PipelineStep> steps, string? error, long totalElapsedMs)
    {
        if (steps is null) steps = Array.Empty<PipelineStep>();

        // Index actual steps by canonical name. Multiple PipelineStep rows can map to the
        // same descriptor (retries, sub-questions) — we keep the FIRST one as the canonical
        // representative for the graph node, and the rest become SubSteps under it.
        var byCanonical = new Dictionary<string, List<PipelineStep>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in steps)
        {
            var key = PipelineArchitecture.CanonicalNameOf(s.Stage);
            if (string.IsNullOrEmpty(key)) continue;
            if (!byCanonical.TryGetValue(key, out var list))
            {
                list = new List<PipelineStep>();
                byCanonical[key] = list;
            }
            list.Add(s);
        }

        // Detect path: which branch of the router fired? Determined by which stages actually
        // emitted a step — not by guesswork on the actual response.
        var path = DetectPath(byCanonical);
        var outcome = !string.IsNullOrEmpty(error)
            ? "failed"
            : steps.Any(s => string.Equals(s.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase))
                ? "refused"
                : "ok";

        var nodes = new List<WorkflowNode>(PipelineArchitecture.Stages.Count);
        foreach (var d in PipelineArchitecture.Stages)
        {
            var fired = byCanonical.TryGetValue(d.CanonicalName, out var stepList) && stepList.Count > 0;
            var onPath = fired || IsOnPathByBranch(d, path);
            var rep = fired ? stepList![0] : null;

            // SubSteps: a stage's recorded PipelineStep.SubSteps[] (LLM call, SQL exec, tool
            // dispatch) become child nodes. Retried versions of the SAME stage also become
            // siblings under the canonical node so the user sees attempt-1, attempt-2 inline.
            var subs = new List<WorkflowNode>();
            if (fired)
            {
                // Sub-steps inside the representative step.
                if (rep!.SubSteps is { Count: > 0 })
                {
                    foreach (var ss in rep.SubSteps)
                        subs.Add(WrapSubStep(ss));
                }
                // Additional retries of the same stage.
                for (int i = 1; i < stepList!.Count; i++)
                    subs.Add(WrapAsNode(stepList[i], suffix: $" (attempt {i + 1})"));
            }

            var (kind, input, output, reason, detailsJson) = rep is not null
                ? ParsePayload(rep.TechnicalData)
                : (rep?.Kind, null, null, null, null);

            nodes.Add(new WorkflowNode(
                Id: d.CanonicalName,
                Label: d.Label,
                Description: d.Description,
                Section: d.Section,
                BranchColumn: d.BranchColumn,
                Mandatory: d.Mandatory,
                Status: ComputeStatus(d, fired, onPath, rep),
                OnPath: onPath,
                Fired: fired,
                ElapsedMs: rep?.ElapsedMs,
                Kind: kind ?? rep?.Kind,
                Input: input,
                Output: output,
                Reason: reason,
                DetailsJson: detailsJson,
                SubSteps: subs));
        }

        var edges = BuildEdges(nodes, path);

        var llmCalls = steps.Count(s => string.Equals(s.Kind, StageNames.KindLlmCall, StringComparison.OrdinalIgnoreCase));

        return new WorkflowGraph(nodes, edges, path, outcome, totalElapsedMs, llmCalls);
    }

    private static string DetectPath(IReadOnlyDictionary<string, List<PipelineStep>> byCanonical)
    {
        // Order matters — first router-probe terminal stage to have fired wins. Refusal /
        // error are detected by the rail-pre guards.
        if (byCanonical.ContainsKey("writeintentguard") &&
            byCanonical["writeintentguard"].Any(s => string.Equals(s.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase)))
            return "Refusal";
        if (byCanonical.ContainsKey("conversational")) return "GeneralChat";
        if (byCanonical.ContainsKey("knowledgematch")) return "GeneralChat";
        if (byCanonical.ContainsKey("semanticsearch")) return "SemanticSearch";
        if (byCanonical.ContainsKey("tooldispatch")) return "ExternalTool";
        if (byCanonical.ContainsKey("verifiedquery")) return "VerifiedQuery";
        if (byCanonical.ContainsKey("specextractor") || byCanonical.ContainsKey("compiler"))
            return "DataQuery";
        return "Error";
    }

    private static bool IsOnPathByBranch(PipelineStageDescriptor d, string path)
    {
        if (d.Section is PipelineArchitecture.SectionRailPre or
                          PipelineArchitecture.SectionRailPost or
                          PipelineArchitecture.SectionDecomposer or
                          PipelineArchitecture.SectionTerminal)
            return true;
        if (d.Section == PipelineArchitecture.SectionRouterProbe)
            return true; // every probe is on the path until one terminates
        if (d.Section == PipelineArchitecture.SectionBranch)
            return string.Equals(d.BranchColumn, path, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string ComputeStatus(PipelineStageDescriptor d, bool fired, bool onPath, PipelineStep? rep)
    {
        if (!onPath) return "not-on-path";
        if (!fired) return d.Mandatory ? "skipped" : "passthrough";
        if (rep is null) return "passthrough";
        return string.Equals(rep.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase) ? "failed" : "ok";
    }

    private static IReadOnlyList<WorkflowEdge> BuildEdges(IReadOnlyList<WorkflowNode> nodes, string path)
    {
        // Sequential edges within the same section + cross-section edges by canonical order.
        // Frontend can read WasTaken to color "taken" edges differently from "available but
        // not taken". This is generic — no per-deployment edge maps to maintain.
        var edges = new List<WorkflowEdge>(nodes.Count);
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            var from = nodes[i];
            var to = nodes[i + 1];
            // Branch-section edges only connect within the same BranchColumn.
            if (from.Section == PipelineArchitecture.SectionBranch &&
                to.Section == PipelineArchitecture.SectionBranch &&
                !string.Equals(from.BranchColumn, to.BranchColumn, StringComparison.OrdinalIgnoreCase))
                continue;
            edges.Add(new WorkflowEdge(
                FromId: from.Id,
                ToId: to.Id,
                WasTaken: from.Fired && to.OnPath,
                Condition: null));
        }
        return edges;
    }

    private static WorkflowNode WrapAsNode(PipelineStep s, string suffix = "")
    {
        var (kind, input, output, reason, detailsJson) = ParsePayload(s.TechnicalData);
        return new WorkflowNode(
            Id: $"{PipelineArchitecture.CanonicalNameOf(s.Stage)}{suffix}",
            Label: s.Stage + suffix,
            Description: s.Detail ?? string.Empty,
            Section: PipelineArchitecture.SectionBranch,
            BranchColumn: null,
            Mandatory: false,
            Status: string.Equals(s.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase) ? "failed" : "ok",
            OnPath: true,
            Fired: true,
            ElapsedMs: s.ElapsedMs,
            Kind: kind ?? s.Kind,
            Input: input,
            Output: output,
            Reason: reason,
            DetailsJson: detailsJson,
            SubSteps: Array.Empty<WorkflowNode>());
    }

    private static WorkflowNode WrapSubStep(PipelineStep s)
    {
        var (kind, input, output, reason, detailsJson) = ParsePayload(s.TechnicalData);
        return new WorkflowNode(
            Id: $"sub-{PipelineArchitecture.CanonicalNameOf(s.Stage)}",
            Label: s.Stage,
            Description: s.Detail ?? string.Empty,
            Section: "sub-step",
            BranchColumn: null,
            Mandatory: false,
            Status: string.Equals(s.Status, StageNames.StatusFailed, StringComparison.OrdinalIgnoreCase) ? "failed" : "ok",
            OnPath: true,
            Fired: true,
            ElapsedMs: s.ElapsedMs,
            Kind: kind ?? s.Kind,
            Input: input,
            Output: output,
            Reason: reason,
            DetailsJson: detailsJson,
            SubSteps: Array.Empty<WorkflowNode>());
    }

    // Parses the structured StepPayload v1 JSON from a step's TechnicalData. Same algorithm
    // the Razor view's parsePayload uses today — extracted to the backend so the frontend can
    // drop its inline implementation.
    private static (string? Kind, string? Input, string? Output, string? Reason, string? DetailsJson)
        ParsePayload(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null, null, null, null);
        var trimmed = raw!.TrimStart();
        if (!trimmed.StartsWith("{")) return (null, null, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("_payload", out var ver) || ver.GetString() != "v1")
                return (null, null, null, null, null);
            string? Get(string n) => root.TryGetProperty(n, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString() : null;
            string? detailsJson = null;
            if (root.TryGetProperty("details", out var det) && det.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                detailsJson = System.Text.Json.JsonSerializer.Serialize(det,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            return (Get("kind"), Get("input"), Get("output"), Get("reason"), detailsJson);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }
}
