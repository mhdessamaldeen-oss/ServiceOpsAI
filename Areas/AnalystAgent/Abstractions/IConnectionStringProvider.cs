namespace AnalystAgent.Abstractions;

/// <summary>
/// Internal config contract for the SQL Server connection string. The host adapter reads
/// <c>ConnectionStrings:DefaultConnection</c> from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// When extracted to a DLL, a different host can provide a different implementation.
/// </summary>
public interface IConnectionStringProvider
{
    string GetConnectionString();
}
