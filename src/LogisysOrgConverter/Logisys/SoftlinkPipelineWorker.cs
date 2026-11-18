using LogisysOrgConverter.Data;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Logisys;

/// <summary>
/// Phase B background host: after convert enqueues a run, opens Softlink Edge (like the old
/// Sign-in button), waits for Security Code / Log In, then runs SoftlinkPipeline unchanged.
/// </summary>
public sealed class SoftlinkPipelineWorker : BackgroundService
{
    private readonly SoftlinkPipelineQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SoftlinkPipelineWorker> _logger;

    public SoftlinkPipelineWorker(
        SoftlinkPipelineQueue queue,
        IServiceScopeFactory scopes,
        ILogger<SoftlinkPipelineWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueueRecentPendingAsync(stoppingToken);

        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _queue.SetRunning(runId);
            _logger.LogInformation("Softlink pipeline starting for run {RunId}", runId);

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var softlink = scope.ServiceProvider.GetRequiredService<LogisysBrowserSession>();
                var pipeline = scope.ServiceProvider.GetRequiredService<SoftlinkPipeline>();
                var recorder = scope.ServiceProvider.GetRequiredService<LogisysOrgConverter.Logging.SoftlinkRecorder>();

                // Film from before sign-in so the recording covers Logi-Sys login to the last step.
                await recorder.StartAsync(runId, stoppingToken);

                if (softlink.IsConfigured)
                {
                    _queue.SetRunning(
                        runId,
                        "Opening Softlink Edge — User Id/Password are filled from config; enter Security Code, Log In, Submit for Gurugram.");
                    _logger.LogInformation(
                        "Opening Softlink Edge for run {RunId}; autofilling User Id/Password, waiting for Security Code + Log In if needed.",
                        runId);
                    await softlink.EnsureReadyAsync(stoppingToken);
                    _queue.SetRunning(runId, "Softlink signed in — running Upload → Created → Finish → India G/L.");
                }

                SoftlinkPipelineSummary summary;
                try
                {
                    summary = await pipeline.RunAsync(runId, stoppingToken);
                }
                finally
                {
                    // Stop on failure too - a recording of a failed run is the useful one.
                    await recorder.StopAsync();
                }

                _queue.SetSucceeded(runId, summary.Message);
                _logger.LogInformation("Softlink pipeline finished for run {RunId}: {Message}", runId, summary.Message);
                await WriteStatusLogAsync(scope.ServiceProvider, runId, summary.Message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _queue.SetFailed(runId, "Softlink pipeline was cancelled because the app is shutting down.");
                throw;
            }
            catch (Exception ex)
            {
                var message = FirstLine(ex.Message);
                _queue.SetFailed(runId, message);
                _logger.LogError(ex, "Softlink pipeline failed for run {RunId}", runId);
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await WriteStatusLogAsync(scope.ServiceProvider, runId, message, stoppingToken);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "Could not write failure status log for run {RunId}.", runId);
                }
            }
        }
    }

    /// <summary>
    /// If convert finished but folder-write hung before Enqueue, pick up recent India/Overseas runs.
    /// </summary>
    private async Task EnqueueRecentPendingAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Only the newest convert with Ready India/Overseas rows — not a backlog of old runs.
            var since = DateTime.UtcNow.AddHours(-6);
            var pendingId = await db.Runs.AsNoTracking()
                .Where(run => run.CreatedAtUtc >= since
                    && (run.IndiaCount > 0 || run.OverseasCount > 0))
                .OrderByDescending(run => run.CreatedAtUtc)
                .Select(run => run.Id)
                .FirstOrDefaultAsync(stoppingToken);

            if (pendingId == Guid.Empty)
            {
                return;
            }

            var needsUpload = await db.RunRows.AsNoTracking().AnyAsync(
                row => row.RunId == pendingId
                    && row.Status == RowStatuses.Ready
                    && row.ImportStatus == null
                    && (row.Template == RowTemplates.India || row.Template == RowTemplates.Overseas),
                stoppingToken);
            if (!needsUpload)
            {
                return;
            }

            _logger.LogInformation(
                "Enqueueing latest pending Softlink run {RunId} (convert finished; upload not done).",
                pendingId);
            _queue.Enqueue(pendingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue recent pending Softlink runs.");
        }
    }

    private static async Task WriteStatusLogAsync(
        IServiceProvider services,
        Guid runId,
        string? message,
        CancellationToken cancellationToken)
    {
        var outputs = services.GetRequiredService<RunOutputWriter>();
        await outputs.WriteAfterPipelineAsync(runId, message, cancellationToken);
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Softlink pipeline failed.";
        }

        var line = message.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(line) ? "Softlink pipeline failed." : line;
    }
}
