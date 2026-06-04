namespace AnalystAgent.Abstractions;

using AnalystAgent.Models;

public interface IValidator
{
    ValidationResult Validate(CompiledSql compiled);
}
