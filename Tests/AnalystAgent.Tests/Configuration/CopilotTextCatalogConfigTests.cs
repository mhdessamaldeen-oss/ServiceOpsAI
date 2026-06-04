namespace AnalystAgent.Tests.Configuration;

using System;
using System.IO;
using System.Text.Json;
using AnalystAgent.Configuration;
using Xunit;

/// <summary>
/// Guards the 2026-06-02 wiring + genericization of <see cref="CopilotTextCatalog"/>:
/// <list type="number">
///   <item>The per-deployment override file <c>copilot-text.json</c> reproduces the worked-examples
///   few-shot BYTE-FOR-BYTE versus the constant that shipped as the in-code default through the 94.3%
///   baseline — so moving the examples into config is provably behavior-neutral for the live planner
///   (no LLM eval needed).</item>
///   <item>The shipped in-code default is now schema-agnostic (placeholders only, zero domain vocabulary),
///   so a fresh deployment never inherits this schema's table/column names.</item>
///   <item>The override file is correctly shaped for the bound section and supplies the ★ planner rules
///   (which were inert until Program.cs started loading this file).</item>
/// </list>
/// The override file is read directly with System.Text.Json (the test project does not reference the
/// Configuration binder); navigation follows <see cref="CopilotTextCatalog.SectionName"/> exactly, so the
/// JSON nesting is verified to match what IOptionsMonitor binds at runtime.
/// </summary>
public class CopilotTextCatalogConfigTests
{
    private static string RepoConfigPath(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ServiceOpsAI.sln")))
            d = d.Parent;
        return d is null ? file : Path.Combine(d.FullName, "Areas", "AnalystAgent", "Configuration", file);
    }

    /// <summary>The override file's bound section (AnalystAgent → Text), navigated via SectionName.</summary>
    private static JsonElement TextSection()
    {
        var json = File.ReadAllText(RepoConfigPath("copilot-text.json"));
        using var doc = JsonDocument.Parse(json);
        var el = doc.RootElement;
        foreach (var part in CopilotTextCatalog.SectionName.Split(':'))
            el = el.GetProperty(part);
        return el.Clone();
    }

    private static string OverrideValue(string property) =>
        TextSection().GetProperty(property).GetString()!;

    [Fact]
    public void OverrideFile_ReproducesShippedWorkedExamples_ByteForByte()
    {
        // The live planner reads SpecExtractorWorkedExamples from this file (via IOptionsMonitor); it MUST
        // equal the block that shipped as the in-code default, or the 94.3% baseline prompt has shifted.
        Assert.Equal(CopilotTextCatalog.ShippedWorkedExamplesReference, OverrideValue("SpecExtractorWorkedExamples"));
    }

    [Fact]
    public void OverrideWorkedExamples_Override_The_SchemaAgnosticDefault()
    {
        // Sanity: the override is NOT just a copy of the generic default — it carries the concrete examples.
        Assert.NotEqual(CopilotTextCatalog.SchemaAgnosticWorkedExamplesDefault, OverrideValue("SpecExtractorWorkedExamples"));
    }

    [Fact]
    public void ShippedDefault_IsSchemaAgnostic_NoDomainVocabularyLeak()
    {
        var def = CopilotTextCatalog.SchemaAgnosticWorkedExamplesDefault;
        foreach (var leak in new[]
        {
            "Tickets", "Bills", "Customers", "Regions", "MeterReadings", "Outages", "CsatResponses",
            "Departments", "ServiceTypes", "AspNetUsers", "TicketStatuses", "TicketCategories",
            "TicketPriorities", "Aleppo", "Damascus", "Houri",
        })
            Assert.DoesNotContain(leak, def, StringComparison.Ordinal);

        Assert.Contains("<RootEntity>", def, StringComparison.Ordinal);   // proves it IS the placeholder set
    }

    [Fact]
    public void OverrideFile_Activates_StarPlannerRules_ThatAreEmptyInCode()
    {
        // SpecExtractorExtraGuidance's in-code default is "" — the ★ rules (filter-values-are-literal,
        // COUNT-is-aggregation) live ONLY in this file. They were inert until Program.cs began loading it.
        var extra = OverrideValue("SpecExtractorExtraGuidance");
        Assert.False(string.IsNullOrWhiteSpace(extra));
        Assert.Contains("★", extra, StringComparison.Ordinal);
        Assert.Equal(string.Empty, new CopilotTextCatalog().SpecExtractorExtraGuidance);
    }
}
