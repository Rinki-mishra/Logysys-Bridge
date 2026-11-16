using System.Security.Cryptography;
using System.Text.Json;
using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.Notifications;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Logisys;

/// <summary>
/// Loads the organisation response spreadsheet from the mailbox Spreadsheet folder
/// (same FindSharedSheetAsync + GoogleSheets.DownloadAsync path as “Load spreadsheet from mail”),
/// converts with existing ConvertAsync, then enqueues SoftlinkPipeline.
/// Skips when the downloaded workbook bytes are unchanged since the last successful convert.
/// </summary>
public sealed class MailSpreadsheetPoller : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AutomationOptions _options;
    private readonly FormMailbox _mailbox;
    private readonly GoogleSheets _google;
    private readonly SoftlinkPipelineQueue _pipelineQueue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MailSpreadsheetPoller> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;

    public MailSpreadsheetPoller(
        IOptions<AutomationOptions> options,
        FormMailbox mailbox,
        GoogleSheets google,
        SoftlinkPipelineQueue pipelineQueue,
        IServiceScopeFactory scopes,
        IWebHostEnvironment environment,
        ILogger<MailSpreadsheetPoller> logger)
    {
        _options = options.Value;
        _mailbox = mailbox;
        _google = google;
        _pipelineQueue = pipelineQueue;
        _scopes = scopes;
        _environment = environment;
        _logger = logger;
        _statePath = Path.Combine(environment.ContentRootPath, "App_Data", "automation-mail-state.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.PollMailbox)
        {
            _logger.LogInformation(
                "Mailbox spreadsheet poller is disabled (Automation:Enabled / PollMailbox).");
            return;
        }

        if (!_mailbox.IsConfigured)
        {
            _logger.LogWarning(
                "Mailbox spreadsheet poller is on but Microsoft mailbox settings are not configured.");
            return;
        }

        var minutes = Math.Clamp(_options.MailPollMinutes, 1, 180);
        _logger.LogInformation(
            "Polling mailbox Spreadsheet folder every {Minutes} minute(s) for Organisation Creation responses.",
            minutes);

        // First pass shortly after startup, then on the interval.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        await TryPollAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryPollAsync(stoppingToken);
        }
    }

    private async Task TryPollAsync(CancellationToken stoppingToken)
    {
        if (!await _gate.WaitAsync(0, stoppingToken))
        {
            return;
        }

        try
        {
            await PollOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mailbox spreadsheet poll failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        // Same entry point as Index “Load spreadsheet from mail”.
        var notice = await _mailbox.FindSharedSheetAsync(stoppingToken);
        var bytes = await _google.DownloadAsync(
            FormSourceKinds.Sheet, notice.SourceId, stoppingToken, notice.OpenUrl);
        EnsureWorkbook(bytes);

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var previous = await ReadStateAsync(stoppingToken);
        if (previous is not null
            && string.Equals(previous.SourceId, notice.SourceId, StringComparison.Ordinal)
            && string.Equals(previous.ContentSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Mailbox spreadsheet unchanged ({SourceId}); skipping convert.",
                notice.SourceId);
            return;
        }

        _logger.LogInformation(
            "Mailbox spreadsheet changed or new ({FileName}, {SourceId}); converting.",
            notice.FileName,
            notice.SourceId);

        Guid runId;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ConversionStore>();
            var outputs = scope.ServiceProvider.GetRequiredService<RunOutputWriter>();
            await using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            runId = await store.ConvertAsync(notice.FileName, stream, stoppingToken);
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
        }

        await WriteStateAsync(
            new MailPollState(notice.SourceId, hash, runId, DateTime.UtcNow, notice.FileName),
            stoppingToken);

        _logger.LogInformation(
            "Mailbox spreadsheet converted to run {RunId} ({FileName}).",
            runId,
            notice.FileName);
    }

    private static void EnsureWorkbook(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x50 || bytes[1] != 0x4B)
        {
            throw new ClientSheetException(
                "Google did not return Organisation Creation Request Form (Responses) as an Excel file.");
        }
    }

    private async Task<MailPollState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<MailPollState>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read automation mail state; will convert on this poll.");
            return null;
        }
    }

    private async Task WriteStateAsync(MailPollState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }

    private sealed record MailPollState(
        string SourceId,
        string ContentSha256,
        Guid LastRunId,
        DateTime ProcessedAtUtc,
        string? FileName);
}
