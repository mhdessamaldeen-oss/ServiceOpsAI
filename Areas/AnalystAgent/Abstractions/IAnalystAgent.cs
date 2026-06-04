namespace AnalystAgent.Abstractions;

using AnalystAgent.Models;

/// <summary>
/// Public surface of the v2 copilot. Single entry point used by the host's HTTP layer.
/// </summary>
public interface IAnalystAgent
{
    Task<AnalystResponse> AskAsync(AnalystRequest request, CancellationToken cancellationToken = default);
}
