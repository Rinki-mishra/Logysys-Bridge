using LogisysOrgConverter.Data;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Logisys;

/// <summary>
/// Phase C: watch an inbox folder for client .xlsx files, run existing ConvertAsync,
/// then enqueue SoftlinkPipeline (Phase B). No convert/Softlink rule changes.
/// </summary>
public sealed class ClientWorkbookWatcher : BackgroundService
{
    private readonly AutomationOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly SoftlinkPipelineQueue _pipelineQueue;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ClientWorkbookWatcher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientWorkbookWatcher(
        IOptions<AutomationOptions> options,
        IServiceScopeFactory scopes,
        SoftlinkPipelineQueue pipelineQueue,
        IWebHostEnvironment environment,
        ILogger<ClientWorkbookWatcher> logger)
    {
        _options = options.Value;
        _scopes = scopes;
        _pipelineQueue = pipelineQueue;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.WatchInboxFolder)
        {
            _logger.LogInformation(
                "Client workbook folder watcher is disabled (Automation:Enabled / WatchInboxFolder).");
            return;
        }

        var inbox = Resolve(_options.InboxFolder);
        var processed = Resolve(_options.ProcessedFolder);
        var failed = Resolve(_options.FailedFolder);
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(processed);
        Directory.CreateDirectory(failed);

        _logger.LogInformation(
            "Watching for client workbooks in {Inbox}. Drop an .xlsx here to convert and run the Softlink pipeline.",
            inbox);

        using var watcher = new FileSystemWatcher(inbox)
        {
            Filter = "*.xlsx",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, args) => _ = HandleAsync(args.FullPath, processed, failed, stoppingToken);
        watcher.Changed += (_, args) => _ = HandleAsync(args.FullPath, processed, failed, stoppingToken);
        watcher.Renamed += (_, args) => _ = HandleAsync(args.FullPath, processed, failed, stoppingToken);

        // Pick up anything already sitting in the inbox when the app starts.
        foreach (var existing in Directory.EnumerateFiles(inbox, "*.xlsx", SearchOption.TopDirectoryOnly))
        {
            _ = HandleAsync(existing, processed, failed, stoppingToken);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(string path, string processedFolder, string failedFolder, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested || !IsClientWorkbook(path))
        {
            return;
        }

        await _gate.WaitAsync(stoppingToken);
        try
        {
            if (!File.Exists(path) || !IsClientWorkbook(path))
            {
                return;
            }

            if (!await WaitUntilReadableAsync(path, stoppingToken))
            {
                _logger.LogWarning("Could not open workbook (still locked): {Path}", path);
                return;
            }

            _logger.LogInformation("Converting inbox workbook {Path}", path);
            Guid runId;
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<ConversionStore>();
                var outputs = scope.ServiceProvider.GetRequiredService<RunOutputWriter>();
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                runId = await store.ConvertAsync(Path.GetFileName(path), stream, stoppingToken);
                try
                {
                    await outputs.WriteAfterConvertAsync(runId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not write run output folder for {RunId}.", runId);
                }

                var run = await store.GetRunAsync(runId, stoppingToken);
                if (run is not null && (run.IndiaCount > 0 || run.OverseasCount > 0))
                {
                    _pipelineQueue.Enqueue(runId);
                }
                else
                {
                    try
                    {
                        await outputs.WriteAfterPipelineAsync(
                            runId,
                            "Softlink pipeline skipped — no India/Overseas organisations to upload.",
                            stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not write final run-status for {RunId}.", runId);
                    }
                }
            }

            MoveAside(path, processedFolder, runId);
            _logger.LogInformation(
                "Inbox workbook converted to run {RunId} and moved to {Folder}",
                runId,
                processedFolder);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inbox workbook failed: {Path}", path);
            try
            {
                if (File.Exists(path))
                {
                    MoveAside(path, failedFolder, null);
                }
            }
            catch (Exception moveEx)
            {
                _logger.LogError(moveEx, "Could not move failed workbook {Path}", path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));

    private static bool IsClientWorkbook(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith("~$", StringComparison.Ordinal)
            || name.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        return name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitUntilReadableAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 0)
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private static void MoveAside(string path, string folder, Guid? runId)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var suffix = runId is { } id ? $"-{id:N}"[..9] : string.Empty;
        var target = Path.Combine(folder, $"{name}-{stamp}{suffix}{ext}");
        if (File.Exists(target))
        {
            target = Path.Combine(folder, $"{name}-{stamp}-{Guid.NewGuid():N}{ext}");
        }

        File.Move(path, target, overwrite: false);
    }
}
