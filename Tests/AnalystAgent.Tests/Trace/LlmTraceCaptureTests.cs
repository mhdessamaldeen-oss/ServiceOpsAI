namespace AnalystAgent.Tests.Trace;

using AnalystAgent.Abstractions;
using Xunit;

/// <summary>
/// Pins the trace-capture policy that the "cover every LLM call" work rides on:
/// (1) Preview mode (normal chat) keeps only the bounded preview, never full text;
/// (2) a Full scope (eval/assessment runs) additionally captures the untruncated prompt + response,
///     itself bounded by the full cap; (3) the scope restores the previous mode on dispose; and
/// (4) the Kind tag (llm vs embedding) flows through. One helper owns truncation for every call site.
/// </summary>
public class LlmTraceCaptureTests
{
    [Fact]
    public void Preview_Truncates_AndOmitsFullText_ByDefault()
    {
        var prompt = new string('p', 5000);
        var resp = new string('r', 5000);

        var rec = LlmTraceCapture.BuildRecord(
            "Emitter", "Ollama", "qwen", usage: null, elapsedMs: 10, success: true, error: null,
            prompt: prompt, response: resp, previewCap: 4000, fullCap: 32000);

        Assert.Equal(4000, rec.PromptPreview!.Length);   // preview bounded
        Assert.Equal(4000, rec.ResponsePreview!.Length);
        Assert.Equal(5000, rec.PromptFullLength);        // full length recorded for the "X/Y" indicator
        Assert.Equal(5000, rec.ResponseFullLength);
        Assert.Null(rec.PromptFull);                     // NO full text outside a Full scope
        Assert.Null(rec.ResponseFull);
        Assert.Equal("llm", rec.Kind);                   // default kind
    }

    [Fact]
    public void FullScope_CapturesUntruncatedText_PreviewStillBounded()
    {
        var prompt = new string('p', 5000);

        using (LlmTraceCaptureScope.Full())
        {
            var rec = LlmTraceCapture.BuildRecord(
                "Emitter", "Ollama", "qwen", usage: null, elapsedMs: 10, success: true, error: null,
                prompt: prompt, response: "ok", previewCap: 4000, fullCap: 32000);

            Assert.Equal(4000, rec.PromptPreview!.Length); // preview still bounded
            Assert.Equal(5000, rec.PromptFull!.Length);    // ...but the FULL prompt is captured whole
            Assert.Equal("ok", rec.ResponseFull);
        }

        // Back in Preview mode after the scope disposes → no full text.
        var after = LlmTraceCapture.BuildRecord(
            "Emitter", "Ollama", "qwen", usage: null, elapsedMs: 10, success: true, error: null,
            prompt: prompt, response: "ok", previewCap: 4000, fullCap: 32000);
        Assert.Null(after.PromptFull);
    }

    [Fact]
    public void FullScope_FullText_BoundedByFullCap()
    {
        var prompt = new string('p', 50000);
        using (LlmTraceCaptureScope.Full())
        {
            var rec = LlmTraceCapture.BuildRecord(
                "Emitter", "Ollama", "qwen", usage: null, elapsedMs: 1, success: true, error: null,
                prompt: prompt, response: null, previewCap: 4000, fullCap: 32000);
            Assert.Equal(32000, rec.PromptFull!.Length);   // never unbounded
        }
    }

    [Fact]
    public void Scope_RestoresPreviousMode_OnDispose()
    {
        Assert.Equal(LlmTraceCaptureMode.Preview, LlmTraceCaptureScope.Current);
        using (LlmTraceCaptureScope.Full())
            Assert.Equal(LlmTraceCaptureMode.Full, LlmTraceCaptureScope.Current);
        Assert.Equal(LlmTraceCaptureMode.Preview, LlmTraceCaptureScope.Current);
    }

    [Fact]
    public void EmbeddingKind_FlowsThrough()
    {
        var rec = LlmTraceCapture.BuildRecord(
            "Embedding", "Embedder", "bge-m3", usage: null, elapsedMs: 5, success: true, error: null,
            prompt: "embed this text", response: "[1024-dim vector]", previewCap: 4000, fullCap: 32000,
            kind: "embedding");
        Assert.Equal("embedding", rec.Kind);
        Assert.Equal("embed this text", rec.PromptPreview);
    }
}
