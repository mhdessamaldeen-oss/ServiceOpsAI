namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

/// <summary>
/// Public surface of the v2 copilot. Single entry point used by the host's HTTP layer.
/// </summary>
public interface ISuperAdminCopilot
{
    Task<CopilotResponse> AskAsync(CopilotRequest request, CancellationToken cancellationToken = default);
}
