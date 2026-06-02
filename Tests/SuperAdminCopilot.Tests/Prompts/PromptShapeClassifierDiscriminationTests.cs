namespace SuperAdminCopilot.Tests.Prompts;

using Microsoft.Extensions.Logging.Abstractions;
using SuperAdminCopilot.Pipeline.Prompts;
using Xunit;

/// <summary>
/// Pins the discrimination the 2026-06-02 trivial-skip fix (issue #1) depends on: a genuinely-simple
/// COUNT question classifies as <see cref="PromptShape.COUNT"/> (and may skip the coverage check),
/// while a "top N by metric" question classifies as <see cref="PromptShape.TOPN"/> (never skips), so
/// a weak model collapsing it into a single-value SQL can no longer pass unchecked.
/// </summary>
public class PromptShapeClassifierDiscriminationTests
{
    private static readonly DeterministicPromptShapeClassifier Classifier =
        new(NullLogger<DeterministicPromptShapeClassifier>.Instance);

    [Theory]
    [InlineData("how many tickets do we have", PromptShape.COUNT)]
    [InlineData("count of overdue bills", PromptShape.COUNT)]
    [InlineData("top 5 customers by total billed amount", PromptShape.TOPN)]
    [InlineData("highest spending customers", PromptShape.TOPN)]
    public void Classify_DiscriminatesCountFromTopN(string question, PromptShape expected)
    {
        Assert.Equal(expected, Classifier.Classify(question));
    }

    [Fact]
    public void TopNQuestion_IsNeverClassifiedCount()
    {
        // The exact regression case from the #3 incident.
        Assert.NotEqual(PromptShape.COUNT, Classifier.Classify("top 5 customers by total billed amount"));
    }
}
