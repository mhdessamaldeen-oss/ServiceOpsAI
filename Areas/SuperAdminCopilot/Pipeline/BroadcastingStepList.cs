namespace SuperAdminCopilot.Pipeline;

using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;

/// <summary>
/// A <see cref="List{T}"/> of <see cref="PipelineStep"/> that fires
/// <see cref="IPipelineStepProgressSink.NotifyStepCompleted"/> on every Add. The orchestrator
/// uses this in place of a plain list so every recorded stage automatically flows to the
/// live progress timeline — no per-call-site changes needed. Any new pipeline stage that
/// follows the existing <c>steps.Add(Step(...))</c> pattern is broadcast for free.
/// </summary>
/// <remarks>
/// <para>Add is hidden via <c>new</c>, not overridden — <see cref="List{T}.Add"/> isn't virtual.
/// That's fine: the orchestrator's <c>steps</c> variable is statically typed as
/// <see cref="BroadcastingStepList"/>, so the C# compiler binds <c>steps.Add(...)</c> to this
/// override. The base <c>PersistAsync</c> method receives it as <see cref="List{T}"/> only at
/// the very end where Add is never called again.</para>
/// </remarks>
internal sealed class BroadcastingStepList : List<PipelineStep>
{
    private readonly IPipelineStepProgressSink _sink;
    private readonly ProgressTarget _target;

    public BroadcastingStepList(IPipelineStepProgressSink sink, ProgressTarget target)
    {
        _sink = sink;
        _target = target;
    }

    public new void Add(PipelineStep step)
    {
        base.Add(step);
        _sink.NotifyStepCompleted(_target, step);
    }
}
