namespace SuperAdminCopilot.Pipeline.Routing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Deterministic-only intent router. Runs every <see cref="IRoutingProbe"/> and returns
/// the highest-confidence match. When no probe claims the question (or none clears the
/// confidence threshold), the router defaults to <see cref="IntentLabel.DataQuery"/> —
/// the planner is robust to questions it can't understand (it'll emit a clarification or
/// the orchestrator's downstream guards will refuse it), so handing ambiguous questions
/// to the LLM classifier added latency and wrong answers without earning its keep.
///
/// <para>Refusal is owned by the deterministic preflight regexes (write-intent, secrets,
/// out-of-scope), NOT by the router. Anything that gets past preflight is — by definition —
/// not a refusal case, so the router never emits <see cref="IntentLabel.Refuse"/> on its own.</para>
///
/// <para>Tie-breaking: highest confidence wins; on exact ties the FIRST registered probe wins
/// (DI registration order matters — register more specific probes before broad ones).</para>
/// </summary>
internal sealed class HybridIntentRouter : IIntentRouter
{
    private const double DeterministicThreshold = 0.7;

    private readonly IReadOnlyList<IRoutingProbe> _probes;
    private readonly ILogger<HybridIntentRouter> _logger;

    public HybridIntentRouter(
        IEnumerable<IRoutingProbe> probes,
        ILogger<HybridIntentRouter> logger)
    {
        _probes = probes.ToList();
        _logger = logger;
    }

    public async Task<RouterDecision> ClassifyAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new RouterDecision(IntentLabel.DataQuery, 0.0, "router", "empty question; default to data_query");

        RouterDecision? best = null;
        foreach (var probe in _probes)
        {
            RouterDecision? decision;
            try
            {
                decision = await probe.ProbeAsync(question, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Router] probe '{Probe}' threw — skipping.", probe.Name);
                continue;
            }
            if (decision is null) continue;
            if (best is null || decision.Confidence > best.Confidence)
                best = decision;
        }

        if (best is not null && best.Confidence >= DeterministicThreshold)
        {
            _logger.LogDebug("[Router] deterministic claim: {Intent} ({Conf:F2}) by {Source}",
                best.Intent, best.Confidence, best.Source);
            return best;
        }

        // No probe claimed it. Default to DataQuery — the planner is the right home for
        // anything that survived preflight and didn't match a specialist handler. This is
        // safer than firing an LLM classifier whose output we then have to second-guess.
        _logger.LogDebug("[Router] no probe claimed — defaulting to DataQuery");
        return new RouterDecision(
            IntentLabel.DataQuery, 0.5, "router-default",
            best is null ? "no probe claimed" : $"best probe confidence {best.Confidence:F2} below threshold");
    }
}
