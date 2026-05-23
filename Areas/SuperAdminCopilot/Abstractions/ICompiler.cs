namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

public interface ICompiler
{
    CompiledSql Compile(QuerySpec spec);
}
