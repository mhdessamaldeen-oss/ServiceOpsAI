namespace AnalystAgent.Internal;

using System.Diagnostics;

/// <summary>
/// Distributed-tracing source for the AnalystAgent pipeline. Emits <see cref="Activity"/>
/// instances (.NET's built-in OpenTelemetry-compatible API) for each pipeline stage. Consuming
/// applications can attach an OpenTelemetry listener (or any <see cref="ActivityListener"/>) to
/// export these activities to Jaeger / Prometheus / Application Insights / Datadog / etc. without
/// requiring this package itself to take a hard dependency on the OpenTelemetry SDK.
///
/// <para><b>Why ActivitySource instead of pulling in OpenTelemetry.Extensions.Hosting?</b> The
/// .NET ActivitySource API IS OpenTelemetry-compatible — it's the bridge layer. Consumers who
/// want OTel can register an OpenTelemetryBuilder that listens to <see cref="Name"/>. Consumers
/// who don't care pay zero overhead because activities are no-op when no listener subscribes.
/// This keeps the DLL footprint small and removes a transitive dependency that not every
/// downstream app would want.</para>
///
/// <para><b>Usage pattern at call sites:</b>
/// <code>
/// using var activity = AnalystActivitySource.Instance.StartActivity("Stage.Compiler");
/// activity?.SetTag("question.length", question.Length);
/// // ... do work ...
/// activity?.SetTag("compiler.result", "ok");
/// </code>
/// The <c>?.</c> chain is important: <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <c>null</c> when no listener is registered (the common case), and the null-conditional
/// keeps the no-listener overhead near-zero.</para>
/// </summary>
public static class AnalystActivitySource
{
    /// <summary>Activity source name. Consumers passing this to
    /// <c>tracerProviderBuilder.AddSource(AnalystActivitySource.Name)</c> get all pipeline
    /// activities — OpenTelemetry, Application Insights, and other listeners all consume by name.</summary>
    public const string Name = "AnalystAgent";

    /// <summary>Process-wide shared source. <see cref="ActivitySource"/> is thread-safe and
    /// designed for static-singleton use; allocating per-request would defeat its purpose.</summary>
    public static readonly ActivitySource Instance = new(Name, AssemblyVersion());

    private static string AssemblyVersion()
    {
        var v = typeof(AnalystActivitySource).Assembly.GetName().Version;
        return v?.ToString() ?? "0.0.0";
    }
}
