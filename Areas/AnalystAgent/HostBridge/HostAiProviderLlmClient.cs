namespace AnalystAgent.HostBridge;

using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;

/// <summary>
/// Bridges the new copilot's <see cref="ILlmClient"/> to the host's existing
/// <see cref="IAiProviderFactory"/>. Every LLM call flows through whatever provider the user
/// has selected for the <see cref="AiWorkloadType.Copilot"/> workload in the settings UI —
/// the same workload the active copilot uses, so one settings knob controls chat behavior.
/// THIS IS THE ONLY FILE that depends on host AI types. When this code moves to a DLL,
/// only this file is replaced.
///
/// <para>Each call enforces a per-call timeout sourced from <see cref="AnalystOptions.LlmCallTimeoutSeconds"/>
/// so a hung provider can't deadlock the eval-runner or block a user request indefinitely. The
/// caller's cancellation token is linked with the timeout token; whichever fires first wins.</para>
/// </summary>
internal sealed class HostAiProviderLlmClient : ILlmClient
{
    private readonly IAiProviderFactory _factory;
    private readonly IOptionsMonitor<AnalystOptions> _options;

    public HostAiProviderLlmClient(IAiProviderFactory factory, IOptionsMonitor<AnalystOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    public async Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        return await InvokeAsync(jsonMode: true, systemPrompt, userPrompt, cancellationToken);
    }

    public async Task<string> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        return await InvokeAsync(jsonMode: false, systemPrompt, userPrompt, cancellationToken);
    }

    /// <summary>Shared call path: builds the combined prompt, fires the provider with the per-call
    /// timeout, captures token usage + elapsed into the active <see cref="LlmCallScope"/> if one is
    /// open, and re-throws as <c>TimeoutException</c> on timeout. JSON and text modes only differ
    /// in whether they invoke <see cref="WorkloadAwareProvider.GenerateJsonAsync"/> when available.</summary>
    private async Task<string> InvokeAsync(bool jsonMode, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        // Token budget gate — runs BEFORE the call to prevent runaway retries from racking
        // up tokens past the per-question cap. Cost-gate is post-question (in HostTraceSink)
        // because computing per-call USD requires a DB pricing lookup we'd rather not do on
        // the hot path. Token totals are free (a sum across the scope's records).
        var scope = LlmCallScope.Current;
        var opts = _options.CurrentValue;
        if (scope is not null)
        {
            if (opts.MaxTokensPerQuestion > 0 && scope.TotalPromptTokens + scope.TotalCompletionTokens >= opts.MaxTokensPerQuestion)
                throw new InvalidOperationException(
                    $"LLM token budget exceeded: question has consumed " +
                    $"{scope.TotalPromptTokens + scope.TotalCompletionTokens} tokens (cap: {opts.MaxTokensPerQuestion}).");
        }

        var provider = _factory.GetProviderForWorkload(AiWorkloadType.Copilot);

        var combinedPrompt = string.IsNullOrEmpty(systemPrompt)
            ? userPrompt
            : systemPrompt + "\n\n" + userPrompt;

        var stage = LlmCallStageHint.Current ?? "Llm";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AiProviderResult? result = null;
        string? error = null;
        using var cts = LinkedTimeoutCts(cancellationToken);
        try
        {
            result = (jsonMode && provider is WorkloadAwareProvider workloadAware)
                ? await workloadAware.GenerateJsonAsync(combinedPrompt).WaitAsync(cts.Token)
                : await provider.GenerateAsync(combinedPrompt).WaitAsync(cts.Token);

            if (!result.Success)
            {
                error = result.Error;
                throw new InvalidOperationException($"LLM call failed via host provider: {result.Error}");
            }
            return result.ResponseText ?? string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"timeout after {_options.CurrentValue.LlmCallTimeoutSeconds}s";
            throw new TimeoutException($"LLM call exceeded {_options.CurrentValue.LlmCallTimeoutSeconds}s timeout.");
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            // Best-effort capture into the per-question scope. Never throw from here.
            try
            {
                // Capture prompt + response previews for the investigation page so operators
                // can answer "what did we send" and "what came back" without grepping logs.
                // The full text is truncated to a per-field cap from AnalystOptions; full
                // lengths are recorded so the UI shows "shown 4000 of 12345 chars".
                // Preview always; FULL prompt+response only under an LlmTraceCaptureScope.Full (eval runs).
                // The shared helper owns the truncation policy so every call site records identically.
                LlmCallScope.Current?.Record(LlmTraceCapture.BuildRecord(
                    stage: stage,
                    provider: result?.ProviderType.ToString() ?? "unknown",
                    model: result?.ModelUsed,
                    usage: result?.Usage,
                    elapsedMs: sw.ElapsedMilliseconds,
                    success: result?.Success ?? false,
                    error: error,
                    prompt: combinedPrompt,
                    response: result?.ResponseText,
                    previewCap: opts.LlmTracePreviewMaxChars,
                    fullCap: opts.LlmTraceFullMaxChars));
            }
            catch { /* swallow */ }
        }
    }

    /// <summary>Build a CancellationTokenSource that fires either when the caller cancels OR
    /// when the per-call timeout elapses. Whichever wins, callers see OperationCanceledException
    /// (re-thrown as TimeoutException by the catch block when the original token was NOT cancelled).</summary>
    private CancellationTokenSource LinkedTimeoutCts(CancellationToken caller)
    {
        var timeoutSec = Math.Max(1, _options.CurrentValue.LlmCallTimeoutSeconds);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        return cts;
    }
}
