namespace AnalystAgent.Domain;

/// <summary>
/// Result of an operation that can succeed with <typeparamref name="T"/> or fail with
/// <typeparamref name="TFault"/>. Domain code returns <see cref="Result{T, TFault}"/>;
/// exceptions are reserved for programmer errors (null where the type promised non-null,
/// integer overflow). See ADR-002.
///
/// <para>Construct via <see cref="Ok"/> / <see cref="Fail"/>. Pattern-match via
/// <see cref="IsOk"/> + <see cref="Value"/> / <see cref="Fault"/>.</para>
/// </summary>
public readonly struct Result<T, TFault>
    where T : notnull
    where TFault : notnull
{
    private readonly T _value;
    private readonly TFault _fault;
    private readonly State _state;

    private enum State : byte { Uninitialized = 0, Ok = 1, Fail = 2 }

    private Result(T value, TFault fault, State state)
    {
        _value = value;
        _fault = fault;
        _state = state;
    }

    public bool IsOk => _state == State.Ok;
    public bool IsFault => _state == State.Fail;

    /// <summary>
    /// Throws when the result is a fault, uninitialized (<c>default(Result&lt;,&gt;)</c>), or
    /// the underlying value reference is null. Production code should prefer
    /// <see cref="TryGetValue"/>; this getter is intentionally loud to surface misuse.
    /// </summary>
    public T Value => _state == State.Ok
        ? (_value ?? throw new System.InvalidOperationException("Result.Value: stored value is null."))
        : throw new System.InvalidOperationException(
            _state == State.Uninitialized ? "Result is uninitialized (default-constructed)."
                                          : "Result is a fault; called Value.");

    public TFault Fault => _state == State.Fail
        ? (_fault ?? throw new System.InvalidOperationException("Result.Fault: stored fault is null."))
        : throw new System.InvalidOperationException(
            _state == State.Uninitialized ? "Result is uninitialized (default-constructed)."
                                          : "Result is ok; called Fault.");

    /// <summary>True iff the result is Ok AND the stored value is non-null.</summary>
    public bool TryGetValue(out T value)
    {
        if (_state == State.Ok && _value is not null)
        {
            value = _value;
            return true;
        }
        value = default!;
        return false;
    }

    public bool TryGetFault(out TFault fault)
    {
        if (_state == State.Fail && _fault is not null)
        {
            fault = _fault;
            return true;
        }
        fault = default!;
        return false;
    }

    public static Result<T, TFault> Ok(T value)
    {
        if (value is null) throw new System.ArgumentNullException(nameof(value), "Result.Ok value cannot be null.");
        return new(value, default!, State.Ok);
    }

    public static Result<T, TFault> Fail(TFault fault)
    {
        if (fault is null) throw new System.ArgumentNullException(nameof(fault), "Result.Fail fault cannot be null.");
        return new(default!, fault, State.Fail);
    }

    /// <summary>
    /// Monadic bind — apply <paramref name="next"/> only if this result is ok. Lets pipeline
    /// stages compose: <c>understand.RunAsync(q).Bind(retrieve).Bind(plan).Bind(repair)…</c>
    /// </summary>
    public Result<TOut, TFault> Bind<TOut>(System.Func<T, Result<TOut, TFault>> next) where TOut : notnull
        => IsOk ? next(_value) : Result<TOut, TFault>.Fail(_fault);

    /// <summary>Map the value while preserving fault-vs-ok status. Inverse of <see cref="Bind"/> for pure transforms.</summary>
    public Result<TOut, TFault> Map<TOut>(System.Func<T, TOut> map) where TOut : notnull
        => IsOk ? Result<TOut, TFault>.Ok(map(_value)) : Result<TOut, TFault>.Fail(_fault);

    public override string ToString() => IsOk ? $"Ok({_value})" : $"Fail({_fault})";
}

/// <summary>
/// Convenience static-factory functions for <see cref="Result{T, TFault}"/>. Lets call sites write
/// <c>Result.Ok&lt;Spec, Fault&gt;(spec)</c> without restating both type params.
/// </summary>
public static class Result
{
    public static Result<T, TFault> Ok<T, TFault>(T value)
        where T : notnull where TFault : notnull
        => Result<T, TFault>.Ok(value);

    public static Result<T, TFault> Fail<T, TFault>(TFault fault)
        where T : notnull where TFault : notnull
        => Result<T, TFault>.Fail(fault);
}
