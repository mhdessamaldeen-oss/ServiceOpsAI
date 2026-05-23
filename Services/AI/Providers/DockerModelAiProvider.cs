using ServiceOpsAI.Enums;
using System.Text;
using System.Text.Json;
using ServiceOpsAI.Data;
using ServiceOpsAI.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ServiceOpsAI.Services.AI.Providers
{
    /// <summary>
    /// AI provider implementation that uses 'docker model run' CLI.
    /// No longer requires a running local server.
    /// </summary>
    public class DockerModelAiProvider : IAiProvider
    {
        private readonly DockerLocalProviderOptions _options;
        private readonly ILogger<DockerModelAiProvider> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AiProviderType ProviderType => AiProviderType.DockerLocal;

        public DockerModelAiProvider(
            IOptions<AiProviderSettings> settings,
            ILogger<DockerModelAiProvider> logger,
            IServiceProvider serviceProvider)
        {
            _options = settings.Value.DockerLocal;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        private string GetActiveModel()
        {
            try
            {
                var configuredModel = GetPersistedModelOverride() ?? _options.Model;
                var installedModels = GetInstalledDockerModels();

                if (!string.IsNullOrWhiteSpace(configuredModel) &&
                    installedModels.Any(model => string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)))
                {
                    return configuredModel;
                }

                if (installedModels.Count > 0)
                {
                    return installedModels[0];
                }

                return configuredModel;
            }
            catch
            {
                return _options.Model;
            }
        }

        private string? GetPersistedModelOverride()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetService<ApplicationDbContext>();
                if (dbContext == null)
                {
                    return null;
                }

                return dbContext.SystemSettings
                    .AsNoTracking()
                    .Where(setting => setting.Key == SettingKeys.DockerModel)
                    .Select(setting => setting.Value)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load persisted Docker model override.");
                return null;
            }
        }

        public string ModelName => GetActiveModel();
        public int ContextCapacity => _options.NumCtx > 0 ? _options.NumCtx : 8192;

        public async Task<AiProviderResult> GenerateAsync(string prompt)
        {
            var activeModel = GetActiveModel();
            _logger.LogInformation("Executing Direct Docker Model '{Model}' ({Length} chars)", activeModel, prompt.Length);

            try 
            {
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                processInfo.ArgumentList.Add("model");
                processInfo.ArgumentList.Add("run");
                processInfo.ArgumentList.Add(activeModel);
                processInfo.ArgumentList.Add("--");

                using var process = Process.Start(processInfo);
                if (process == null) throw new Exception("Failed to start docker process");

                // CRITICAL: Start reading stdout/stderr BEFORE writing to stdin.
                // This prevents the classic .NET Process deadlock where the OS pipe
                // buffer fills up, blocking the child process, while we're blocked
                // waiting for exit.
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                // Write the prompt to stdin and close it to signal EOF
                await process.StandardInput.WriteAsync(prompt);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();

                // Wait for the process to exit with a timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                try 
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Docker model execution timed out after {Timeout}s", _options.TimeoutSeconds);
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return new AiProviderResult { Success = false, Error = $"Docker model execution timed out after {_options.TimeoutSeconds}s.", ProviderType = AiProviderType.DockerLocal };
                }

                // Now safely read the completed output
                var responseText = await outputTask;
                var errorText = await errorTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("Docker model exited with code {ExitCode}: {Error}", process.ExitCode, errorText);
                    return new AiProviderResult { Success = false, Error = $"Docker error (exit {process.ExitCode}): {errorText}", ProviderType = AiProviderType.DockerLocal };
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    _logger.LogWarning("Docker model returned empty response. Stderr: {Error}", errorText);
                    return new AiProviderResult { Success = false, Error = "Docker model returned an empty response.", ProviderType = AiProviderType.DockerLocal };
                }

                _logger.LogInformation("Docker model responded successfully ({Length} chars)", responseText.Length);
                return new AiProviderResult
                {
                    Success = true,
                    ResponseText = responseText.Trim(),
                    ProviderType = AiProviderType.DockerLocal
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing docker model");
                return new AiProviderResult { Success = false, Error = ex.Message, ProviderType = AiProviderType.DockerLocal };
            }
        }

        public Task<float[]> GetEmbeddingAsync(string text)
        {
            // Direct Docker CLI doesn't natively support embeddings as easily as a REST API
            _logger.LogWarning("GetEmbeddingAsync not implemented for DockerModelAiProvider.");
            return Task.FromResult(Array.Empty<float>());
        }

        public async Task<(bool IsValid, string? ErrorMessage)> ValidateConfigurationAsync()
        {
            try
            {
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                processInfo.ArgumentList.Add("model");
                processInfo.ArgumentList.Add("ls");

                using var process = Process.Start(processInfo);
                if (process == null) return (false, "Could not start Docker process.");
                
                await process.WaitForExitAsync();
                if (process.ExitCode == 0) return (true, null);

                var error = await process.StandardError.ReadToEndAsync();
                return (false, $"Docker CLI error: {error}");
            }
            catch (Exception ex)
            {
                return (false, $"Docker model infrastructure check failed: {ex.Message}");
            }
        }

        private List<string> GetInstalledDockerModels()
        {
            var processInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            processInfo.ArgumentList.Add("model");
            processInfo.ArgumentList.Add("ls");

            using var process = new Process { StartInfo = processInfo };
            process.Start();

            // Read output and error streams to prevent buffer deadlocks
            var outputTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var errorTask = Task.Run(() => process.StandardError.ReadToEnd());

            if (!process.WaitForExit(5000)) // 5 second timeout for listing models
            {
                try { process.Kill(); } catch { }
                _logger.LogWarning("Docker model ls timed out during provider initialization.");
                return new List<string>();
            }

            var output = outputTask.Result;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new List<string>();
            }

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }

        public async Task<List<ServiceOpsAI.Models.DTOs.AiModelDto>> GetInstalledModelsAsync()
        {
            var results = new List<ServiceOpsAI.Models.DTOs.AiModelDto>();
            try
            {
                var processInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                processInfo.ArgumentList.Add("model");
                processInfo.ArgumentList.Add("ls");

                using var process = new Process { StartInfo = processInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                if (!await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.IsCompletedSuccessfully && !t.IsCanceled))
                {
                    try { process.Kill(); } catch { }
                    return results;
                }

                var output = await outputTask;
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return results;

                var activeModel = ModelName;
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1)
                {
                    foreach (var line in lines.Skip(1))
                    {
                        var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
                        if (parts.Length < 2) parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 1)
                        {
                            var name = parts[0];
                            results.Add(new ServiceOpsAI.Models.DTOs.AiModelDto
                            {
                                Name = name,
                                Size = parts.Length > 1 ? parts[parts.Length - 1] : "Unknown",
                                ParameterSize = parts.Length > 2 ? parts[1] : "",
                                Quantization = parts.Length > 3 ? parts[2] : "",
                                Family = "Docker",
                                IsActive = string.Equals(name, activeModel, StringComparison.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Docker models via IAiProvider");
            }
            return results;
        }
    }
}
