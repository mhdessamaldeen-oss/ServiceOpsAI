namespace SuperAdminCopilot.Abstractions;

using SuperAdminCopilot.Models;

public interface IValidator
{
    ValidationResult Validate(CompiledSql compiled);
}
