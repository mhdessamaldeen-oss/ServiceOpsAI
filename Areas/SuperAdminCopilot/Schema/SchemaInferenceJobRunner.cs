namespace SuperAdminCopilot.Schema;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Singleton coordinator for the user-triggered schema inference job. The UI calls
/// <see cref="StartAsync"/> to kick off generation in a background task; concurrent polling
/// calls read <see cref="State"/> to render a progress bar. Only one job runs at a time.
/// </summary>
public interface ISchemaInferenceJobRunner
{
    SchemaInferenceJobState State { get; }
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    bool Delete();
    Task<string?> ReadFileContentAsync(CancellationToken cancellationToken = default);
}

// SchemaInferenceJobStatus + SchemaInferenceJobState live in SchemaInferenceModels.cs.

internal sealed class SchemaInferenceJobRunner : ISchemaInferenceJobRunner
{
    private readonly IServiceProvider _services;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<SchemaInferenceJobRunner> _logger;
    private readonly object _gate = new();
    private SchemaInferenceJobState _state = new();
    private Task? _runningTask;

    public SchemaInferenceJobRunner(
        IServiceProvider services,
        IOptions<CopilotOptions> options,
        ILogger<SchemaInferenceJobRunner> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public SchemaInferenceJobState State
    {
        get
        {
            lock (_gate)
            {
                // Merge file-on-disk facts into the live state so the UI shows the
                // last-generated metadata even after a restart.
                var path = ResolvePath(_options.Value.SchemaInferredPath);
                var (exists, fileGen, fileCount) = ProbeFile(path);
                return new SchemaInferenceJobState
                {
                    Status = _state.Status,
                    TablesTotal = _state.TablesTotal,
                    TablesDone = _state.TablesDone,
                    CurrentTable = _state.CurrentTable,
                    StartedAt = _state.StartedAt,
                    FinishedAt = _state.FinishedAt,
                    Error = _state.Error,
                    FileExists = exists,
                    FileLastGeneratedAt = fileGen,
                    FileTableCount = fileCount,
                    FilePath = path,
                };
            }
        }
    }

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state.Status == SchemaInferenceJobStatus.Running)
                return Task.FromResult(false);
            _state = new SchemaInferenceJobState
            {
                Status = SchemaInferenceJobStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            };
            // CancellationToken.None — the background job must outlive the HTTP request that
            // started it. Tying to the request token cancels generation as soon as 202 is sent.
            _runningTask = Task.Run(() => RunAsync(CancellationToken.None), CancellationToken.None);
            return Task.FromResult(true);
        }
    }

    public bool Delete()
    {
        var path = ResolvePath(_options.Value.SchemaInferredPath);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("[SuperAdminCopilot] Schema inference file deleted: {Path}", path);
                lock (_gate) { _state = new SchemaInferenceJobState(); }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SuperAdminCopilot] Schema inference delete failed.");
            return false;
        }
    }

    public async Task<string?> ReadFileContentAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(_options.Value.SchemaInferredPath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath(_options.Value.SchemaInferredPath);
        try
        {
            using var scope = _services.CreateScope();
            var generator = scope.ServiceProvider.GetRequiredService<ISchemaInferenceGenerator>();
            var progress = new Progress<SchemaInferenceProgress>(p =>
            {
                lock (_gate)
                {
                    _state = new SchemaInferenceJobState
                    {
                        Status = SchemaInferenceJobStatus.Running,
                        StartedAt = _state.StartedAt,
                        TablesTotal = p.TablesTotal,
                        TablesDone = p.TablesDone,
                        CurrentTable = p.CurrentTable,
                    };
                }
            });
            await generator.WriteAsync(path, progress, cancellationToken);
            // Refresh in-memory schema knowledge so downstream consumers pick up the new file
            // without needing an app restart.
            scope.ServiceProvider.GetService<ISchemaKnowledge>()?.Reload();
            lock (_gate)
            {
                _state = new SchemaInferenceJobState
                {
                    Status = SchemaInferenceJobStatus.Completed,
                    StartedAt = _state.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    TablesTotal = _state.TablesTotal,
                    TablesDone = _state.TablesTotal,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SuperAdminCopilot] Schema inference job failed.");
            lock (_gate)
            {
                _state = new SchemaInferenceJobState
                {
                    Status = SchemaInferenceJobStatus.Failed,
                    StartedAt = _state.StartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message,
                };
            }
        }
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);

    private static (bool exists, DateTimeOffset? generatedAt, int? tableCount) ProbeFile(string path)
    {
        if (!File.Exists(path)) return (false, null, null);
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            DateTimeOffset? gen = doc.RootElement.TryGetProperty("GeneratedAt", out var gp) &&
                                  gp.TryGetDateTimeOffset(out var dt) ? dt : null;
            int? count = doc.RootElement.TryGetProperty("Tables", out var tp) && tp.ValueKind == JsonValueKind.Array
                ? tp.GetArrayLength() : null;
            return (true, gen, count);
        }
        catch
        {
            return (true, null, null);
        }
    }
}
