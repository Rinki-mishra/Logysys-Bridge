using System.Diagnostics;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logging;

public sealed class RecordingOptions
{
    public const string Section = "Recording";

    public bool Enabled { get; set; } = true;

    /// <summary>Absolute, or relative to the app's content root.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>Screenshot cadence for the stitched video.</summary>
    public double FrameSeconds { get; set; } = 1.5;

    /// <summary>Playwright trace (filmstrip + DOM snapshots + every action).</summary>
    public bool Trace { get; set; } = true;

    /// <summary>Stitch the frames into an .mp4 when ffmpeg is on PATH.</summary>
    public bool Video { get; set; } = true;

    /// <summary>Frames per second of the stitched video.</summary>
    public int VideoFps { get; set; } = 4;

    /// <summary>Delete recordings older than this. 0 keeps everything.</summary>
    public int RetainDays { get; set; } = 14;

    /// <summary>Remove the PNG frames once the .mp4 is produced.</summary>
    public bool DeleteFramesAfterVideo { get; set; } = true;
}

/// <summary>
/// Records the Softlink session from sign-in to the last pipeline step. Native Playwright
/// video is unavailable here because we attach to an existing Edge over CDP, so this takes
/// periodic screenshots (stitched to .mp4 when ffmpeg is present) and, alongside them, a
/// Playwright trace that can be scrubbed action by action.
/// </summary>
public sealed class SoftlinkRecorder
{
    private readonly RecordingOptions _options;
    private readonly LogisysBrowserSessionAccessor _accessor;
    private readonly ILogger<SoftlinkRecorder> _logger;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IBrowserContext? _tracing;
    private string _folder = string.Empty;
    private int _frame;

    public SoftlinkRecorder(
        RecordingOptions options,
        LogisysBrowserSessionAccessor accessor,
        IWebHostEnvironment environment,
        ILogger<SoftlinkRecorder> logger)
    {
        _options = options;
        _accessor = accessor;
        _logger = logger;
        _root = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(environment.ContentRootPath, options.Directory);
    }

    public async Task StartAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();
            CleanOld();

            _folder = Path.Combine(_root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{runId.ToString()[..8]}");
            Directory.CreateDirectory(_folder);
            _frame = 0;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
            _logger.LogInformation("Recording Softlink session to {Folder}", _folder);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;

        await StopTraceAsync();
        await StitchVideoAsync();
    }

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0.3, _options.FrameSeconds));
        while (!token.IsCancellationRequested)
        {
            try
            {
                var context = _accessor.Current;
                if (context is not null)
                {
                    await EnsureTraceAsync(context);
                    await CaptureFrameAsync(context, token);
                }
            }
            catch (PlaywrightException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EnsureTraceAsync(IBrowserContext context)
    {
        if (!_options.Trace || _tracing is not null)
        {
            return;
        }

        try
        {
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Title = Path.GetFileName(_folder)
            });
            _tracing = context;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Playwright tracing could not be started.");
            _options.Trace = false;
        }
    }

    private async Task StopTraceAsync()
    {
        if (_tracing is null)
        {
            return;
        }

        try
        {
            await _tracing.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine(_folder, "trace.zip")
            });
            _logger.LogInformation("Softlink trace written to {Path}", Path.Combine(_folder, "trace.zip"));
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Playwright tracing could not be stopped cleanly.");
        }
        finally
        {
            _tracing = null;
        }
    }

    private async Task CaptureFrameAsync(IBrowserContext context, CancellationToken token)
    {
        IPage? page = null;
        foreach (var candidate in context.Pages)
        {
            try
            {
                if (candidate.IsClosed)
                {
                    continue;
                }

                // Prefer whichever Softlink window is in front: Master popup over the list.
                page = candidate;
                if ((candidate.Url ?? string.Empty).Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        if (page is null || token.IsCancellationRequested)
        {
            return;
        }

        var path = Path.Combine(_folder, $"frame-{_frame:D5}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Timeout = 5_000 });
        _frame++;
    }

    private async Task StitchVideoAsync()
    {
        if (!_options.Video || _frame == 0 || _folder.Length == 0)
        {
            return;
        }

        var output = Path.Combine(_folder, "session.mp4");
        var args = $"-y -framerate {Math.Max(1, _options.VideoFps)} -i \"{Path.Combine(_folder, "frame-%05d.png")}\" "
                   + $"-c:v libx264 -pix_fmt yuv420p -vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" \"{output}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && File.Exists(output))
            {
                _logger.LogInformation("Softlink session video written to {Path} ({Frames} frames)", output, _frame);
                if (_options.DeleteFramesAfterVideo)
                {
                    foreach (var frame in Directory.GetFiles(_folder, "frame-*.png"))
                    {
                        try { File.Delete(frame); } catch (IOException) { }
                    }
                }

                return;
            }

            _logger.LogWarning("ffmpeg exited with {Code}; the PNG frames are kept in {Folder}", process.ExitCode, _folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // ffmpeg not installed - leave the frames and say how to turn them into a video.
            await File.WriteAllTextAsync(
                Path.Combine(_folder, "HOW-TO-MAKE-VIDEO.txt"),
                "ffmpeg was not found on PATH, so the frames were not stitched into a video."
                + Environment.NewLine + Environment.NewLine
                + "Install ffmpeg (winget install Gyan.FFmpeg) and run, from this folder:"
                + Environment.NewLine
                + $"  ffmpeg -framerate {Math.Max(1, _options.VideoFps)} -i frame-%05d.png -c:v libx264 -pix_fmt yuv420p session.mp4"
                + Environment.NewLine + Environment.NewLine
                + "trace.zip can be opened straight away at https://trace.playwright.dev"
                + Environment.NewLine);
            _logger.LogInformation("ffmpeg not found; {Frames} frames kept in {Folder}", _frame, _folder);
        }
    }

    private void CleanOld()
    {
        if (_options.RetainDays <= 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_root);
            var cutoff = DateTime.Now.AddDays(-_options.RetainDays);
            foreach (var folder in Directory.GetDirectories(_root))
            {
                if (Directory.GetLastWriteTime(folder) < cutoff)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Lets the recorder see the live Softlink context without owning the session.</summary>
public sealed class LogisysBrowserSessionAccessor
{
    public IBrowserContext? Current { get; set; }
}
