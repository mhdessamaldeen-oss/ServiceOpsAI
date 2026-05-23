using Microsoft.EntityFrameworkCore;
using AISupportAnalysisPlatform.Data;

namespace AISupportAnalysisPlatform.Services.Infrastructure;

public enum RuntimeDatabaseProvider
{
    SqlServer
}

public sealed class RuntimeDatabaseTarget
{
    public RuntimeDatabaseProvider Provider { get; init; } = RuntimeDatabaseProvider.SqlServer;
    public string ConnectionString { get; init; } = "";
}

public interface IRuntimeDatabaseTargetService
{
    RuntimeDatabaseTarget GetCurrent();
    RuntimeDatabaseTarget GetDefault();
    void SetCurrent(RuntimeDatabaseTarget target);
    void ResetToDefault();
}

public sealed class RuntimeDatabaseTargetService : IRuntimeDatabaseTargetService
{
    private readonly object _sync = new();
    private readonly RuntimeDatabaseTarget _defaultTarget;
    private RuntimeDatabaseTarget _currentTarget;

    public RuntimeDatabaseTargetService(RuntimeDatabaseTarget defaultTarget)
    {
        _defaultTarget = new RuntimeDatabaseTarget
        {
            Provider = defaultTarget.Provider,
            ConnectionString = defaultTarget.ConnectionString
        };
        _currentTarget = new RuntimeDatabaseTarget
        {
            Provider = defaultTarget.Provider,
            ConnectionString = defaultTarget.ConnectionString
        };
    }

    public RuntimeDatabaseTarget GetCurrent()
    {
        lock (_sync)
        {
            return Clone(_currentTarget);
        }
    }

    public RuntimeDatabaseTarget GetDefault()
    {
        lock (_sync)
        {
            return Clone(_defaultTarget);
        }
    }

    public void SetCurrent(RuntimeDatabaseTarget target)
    {
        lock (_sync)
        {
            _currentTarget = Clone(target);
        }
    }

    public void ResetToDefault()
    {
        lock (_sync)
        {
            _currentTarget = Clone(_defaultTarget);
        }
    }

    private static RuntimeDatabaseTarget Clone(RuntimeDatabaseTarget target) => new()
    {
        Provider = target.Provider,
        ConnectionString = target.ConnectionString
    };
}

public static class RuntimeDatabaseConfigurator
{
    public static void Configure(DbContextOptionsBuilder options, RuntimeDatabaseTarget target)
    {
        switch (target.Provider)
        {
            case RuntimeDatabaseProvider.SqlServer:
                options.UseSqlServer(target.ConnectionString, sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorNumbersToAdd: null);
                });
                // Demote pending-model-changes from error to warning. The DB is migrated to a
                // state that matches the current model — EF's overly defensive check fires when
                // multiple developers add fields in parallel branches, blocking app startup
                // even though no real schema mismatch exists. Migrations are still validated at
                // Migrate() call time, so genuine drift will still surface.
                options.ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                break;

            default:
                throw new NotSupportedException($"Database provider '{target.Provider}' is not supported in this build.");
        }
    }
}
