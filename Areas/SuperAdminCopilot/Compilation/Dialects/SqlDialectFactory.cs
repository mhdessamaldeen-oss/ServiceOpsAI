namespace SuperAdminCopilot.Compilation.Dialects;

using SuperAdminCopilot.Configuration;

/// <summary>
/// Selects the compiler's <see cref="ISqlDialect"/> from the configured <see cref="DatabaseEngine"/>.
/// The engine-selection keystone — replaces the previously hardcoded DI binding so the dialect is a
/// config choice (<see cref="CopilotOptions.Database"/>), not a recompile. Both dialects are stateless
/// and parameterless; unknown/default engines degrade to <see cref="MssqlDialect"/> (the production target).
/// </summary>
internal static class SqlDialectFactory
{
    public static ISqlDialect Create(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres => new PostgresDialect(),
        _ => new MssqlDialect(),   // SqlServer (default) — current production target
    };
}
