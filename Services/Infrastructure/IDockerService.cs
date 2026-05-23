namespace ServiceOpsAI.Services.Infrastructure;

public interface IDockerService
{
    Task<bool> IsDockerInstalledAsync();
    Task<bool> IsDockerRunningAsync();
    Task<(bool Success, string Message)> LaunchEngineAsync(string engineType);
    Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string command, string args);
}
