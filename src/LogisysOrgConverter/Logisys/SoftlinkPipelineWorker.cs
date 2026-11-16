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
        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _queue.SetRunning(runId);
            _logger.LogInformation("Softlink pipeline starting for run {RunId}", runId);

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var softlink = scope.ServiceProvider.GetRequiredService<LogisysBrowserSession>();
                var pipeline = scope.ServiceProvider.GetRequiredService<SoftlinkPipeline>();

                if (softlink.IsConfigured)
                {
                    _queue.SetRunning(
                        runId,
                        "Opening Softlink Edge — enter Security Code, Log In, Submit for Gurugram when the login page appears.");
                    _logger.LogInformation(
                        "Opening Softlink Edge for run {RunId}; waiting for Security Code + Log In if needed.",
                        runId);
                    await softlink.EnsureReadyAsync(stoppingToken);
                    _queue.SetRunning(runId, "Softlink signed in — running Upload → Created → Finish → India G/L.");
                }

                var summary = await pipeline.RunAsync(runId, stoppingToken);
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
