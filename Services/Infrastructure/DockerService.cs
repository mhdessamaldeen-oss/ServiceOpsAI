using System.Diagnostics;

namespace AISupportAnalysisPlatform.Services.Infrastructure;

public class DockerService : IDockerService
{
    private readonly ILogger<DockerService> _logger;

    public DockerService(ILogger<DockerService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsDockerInstalledAsync()
    {
        try
        {
            var result = await RunCommandAsync("docker", "--version");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsDockerRunningAsync()
    {
        try
        {
            var result = await RunCommandAsync("docker", "info");
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("Docker info check failed with exit code {Code}. Error: {Error}", result.ExitCode, result.Error);
            }
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if Docker is running.");
            return false;
        }
    }

    public async Task<(bool Success, string Message)> LaunchEngineAsync(string engineType)
    {
        string command;
        string args;

        if (engineType.ToLower() == "dockerlocal")
        {
            var isRunning = await IsDockerRunningAsync();
            return isRunning
                ? (true, "Docker Desktop is already running. Docker model support is available without a separate sidecar container.")
                : (false, "Docker Desktop is not running. Start Docker Desktop to use Docker model support.");
        }
        else if (engineType.ToLower() == "localai")
        {
            command = "docker";
            args = "run -d --name supportflow-localai -p 8080:8080 localai/localai:latest";
        }
        else
        {
            return (false, "Unknown engine type");
        }

        try
        {
            var checkResult = await RunCommandAsync("docker", $"ps -a --filter \"name=supportflow-{engineType.ToLower()}\" --format \"{{{{.Names}}}}\"");
            if (!string.IsNullOrWhiteSpace(checkResult.Output))
            {
                await RunCommandAsync("docker", $"start supportflow-{engineType.ToLower()}");
                return (true, $"Engine 'supportflow-{engineType}' was already provisioned and has been started.");
            }

            var result = await RunCommandAsync(command, args);
            if (result.ExitCode == 0)
            {
                return (true, $"Successfully launched {engineType} container.");
            }

            return (false, $"Failed to launch {engineType}. Docker error: {result.Error}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch engine {EngineType}", engineType);
            return (false, $"Error executing docker command: {ex.Message}");
        }
    }

    public async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string command, string args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Read both streams in parallel to avoid deadlock
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
