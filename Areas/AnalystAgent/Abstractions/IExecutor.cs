namespace AnalystAgent.Abstractions;

using AnalystAgent.Models;

public interface IExecutor
{
    Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default);
}
