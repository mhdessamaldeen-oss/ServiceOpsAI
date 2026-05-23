namespace SuperAdminCopilot.Schema;

using Microsoft.Data.SqlClient;
using SuperAdminCopilot.Abstractions;

public interface IDbConnectionFactory
{
    SqlConnection Open();
}

/// <summary>
/// Opens read-only connections to SQL Server using the connection string supplied by
/// <see cref="IConnectionStringProvider"/> (in the host build, this resolves to
/// ConnectionStrings:DefaultConnection — the same DB the host already uses).
/// </summary>
internal sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly IConnectionStringProvider _provider;

    public SqlServerConnectionFactory(IConnectionStringProvider provider) => _provider = provider;

    public SqlConnection Open()
    {
        var conn = new SqlConnection(_provider.GetConnectionString());
        conn.Open();
        return conn;
    }
}
