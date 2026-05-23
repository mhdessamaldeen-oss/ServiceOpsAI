namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

public interface IExecutor
{
    Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default);
}
