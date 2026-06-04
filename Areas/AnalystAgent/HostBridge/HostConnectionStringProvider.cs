namespace AnalystAgent.HostBridge;

using Microsoft.Extensions.Configuration;
using AnalystAgent.Abstractions;

/// <summary>
/// Bridges <see cref="IConnectionStringProvider"/> to the host's
/// <c>ConnectionStrings:DefaultConnection</c>. The new copilot reads the same database the
/// host already points at — no separate connection-string config to maintain.
/// </summary>
internal sealed class HostConnectionStringProvider : IConnectionStringProvider
{
    private readonly IConfiguration _configuration;

    public HostConnectionStringProvider(IConfiguration configuration) => _configuration = configuration;

    public string GetConnectionString()
    {
        var cs = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not set. AnalystAgent requires it.");
        return cs;
    }
}
