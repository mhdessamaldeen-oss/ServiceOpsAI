namespace SuperAdminCopilot.Tests.Compilation;

using System;
using System.IO;
using System.Text.Json;
using SuperAdminCopilot.Compilation.Dialects;
using SuperAdminCopilot.Configuration;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 engine-selection keystone: the compiler's SQL dialect is now chosen from
/// CopilotOptions.Database instead of a hardcoded DI binding. Asserts (1) the factory maps each
/// engine to the right dialect, (2) the default is SqlServer so the live deployment's T-SQL behavior
/// is unchanged, and (3) the copilot-options.json knob binds to a valid engine name (a typo can't
/// silently fall back to the C# default unnoticed).
/// </summary>
public class DatabaseEngineSelectionTests
{
    [Theory]
    [InlineData(DatabaseEngine.SqlServer, typeof(MssqlDialect))]
    [InlineData(DatabaseEngine.Postgres, typeof(PostgresDialect))]
    public void Factory_SelectsDialect_ByEngine(DatabaseEngine engine, Type expected)
        => Assert.IsType(expected, SqlDialectFactory.Create(engine));

    [Fact]
    public void DefaultEngine_IsSqlServer_SoLiveBehaviorIsUnchanged()
        => Assert.Equal(DatabaseEngine.SqlServer, new CopilotOptions().Database);

    [Fact]
    public void CopilotOptionsJson_DatabaseKnob_BindsToValidEngineName()
    {
        var path = RepoConfigPath("copilot-options.json");
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var value = doc.RootElement.GetProperty("SuperAdminCopilot").GetProperty("Database").GetString();

        Assert.True(
            Enum.TryParse<DatabaseEngine>(value, ignoreCase: true, out var engine),
            $"copilot-options.json Database='{value}' must be a valid DatabaseEngine name.");
        Assert.Equal(DatabaseEngine.SqlServer, engine); // shipped default — keep execution on T-SQL
    }

    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "SuperAdminCopilot", "Configuration", file);
    }
}
