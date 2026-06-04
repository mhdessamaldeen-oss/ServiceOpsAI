namespace AnalystAgent.Abstractions;

/// <summary>
/// A user-visible warning emitted when the compiler silently drops part of the LLM's spec
/// (column not in catalog, filter value rejected as a placeholder token). Surfaced in
/// <c>AnalystResponse.Warnings</c> so the user knows their question wasn't fully honored.
///
/// <para>Before warnings existed, drops were silent — the user got a result that LOOKED
/// like an answer but secretly omitted a filter or a column they asked for. Common pattern
/// with weaker local models; this surface is how the user sees the model's mistakes
/// instead of being confused by stealth-wrong answers.</para>
///
/// <para><b>Codes:</b>
/// <list type="bullet">
///   <item><c>UnknownColumn</c> — the LLM referenced a column that doesn't exist on the
///   target table. The filter / select item was dropped.</item>
///   <item><c>RejectedFilterValue</c> — the filter value was a placeholder token
///   (<c>@p0</c>, a JSON envelope, a subquery string) that would crash SQL if parameterized.
///   The filter was dropped instead.</item>
/// </list></para>
/// </summary>
public sealed record AnalystWarning(
    string Code,
    string MessageEn,
    string MessageAr,
    string? Field = null)
{
    public static AnalystWarning UnknownColumn(string column) => new(
        Code: "UnknownColumn",
        MessageEn: $"Filter or column '{column}' was not found and was ignored.",
        MessageAr: $"تم تجاهل العمود أو الفلتر '{column}' لأنه غير موجود في الجدول.",
        Field: column);

    public static AnalystWarning RejectedFilterValue(string column) => new(
        Code: "RejectedFilterValue",
        MessageEn: $"Filter on '{column}' was dropped because the value was not a real literal.",
        MessageAr: $"تم تجاهل التصفية على '{column}' لأن القيمة ليست قيمة حقيقية.",
        Field: column);
}
