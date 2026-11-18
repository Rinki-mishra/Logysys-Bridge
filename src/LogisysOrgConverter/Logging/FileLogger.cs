using System.Collections.Concurrent;
using System.Text;

namespace LogisysOrgConverter.Logging;

/// <summary>
/// Writes every log line the app produces to one complete file, so a whole session
/// (sign-in through the last Softlink step) can be read back afterwards. run-status.log
/// stays as it is - this is the detailed running commentary beside it.
/// </summary>
public sealed class FileLogOptions
{
    public const string Section = "Logging:File";

    public bool Enabled { get; set; } = true;

    /// <summary>Absolute, or relative to the app's content root.</summary>
    public string Directory { get; set; } = "logs";

    public string FileNamePrefix { get; set; } = "logysys-bridge";

    /// <summary>Start a new file each day. One continuous file when false.</summary>
    public bool RollDaily { get; set; } = true;

    /// <summary>Delete rolled files older than this. 0 keeps everything.</summary>
    public int RetainDays { get; set; } = 30;
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogOptions _options;
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 20_000);
    private readonly Thread _writer;
    private DateTime _currentDay = DateTime.MinValue;
    private string _currentPath = string.Empty;

    public FileLoggerProvider(FileLogOptions options, string contentRoot)
    {
        _options = options;
        _directory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(contentRoot, options.Directory);
        System.IO.Directory.CreateDirectory(_directory);
        CleanOldFiles();

        // Background writer: logging must never block the Softlink pipeline.
        _writer = new Thread(DrainQueue)
        {
            IsBackground = true,
            Name = "file-logger"
        };
        _writer.Start();
    }

    public string CurrentPath => ResolvePath();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Enqueue);

    private void Enqueue(string line)
    {
        // Drop rather than block if the writer falls behind.
        _queue.TryAdd(line);
    }

    private void DrainQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(ResolvePath(), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string ResolvePath()
    {
        if (!_options.RollDaily)
        {
            return Path.Combine(_directory, _options.FileNamePrefix + ".log");
        }

        var today = DateTime.Now.Date;
        if (today != _currentDay || _currentPath.Length == 0)
        {
            _currentDay = today;
            _currentPath = Path.Combine(_directory, $"{_options.FileNamePrefix}-{today:yyyyMMdd}.log");
            CleanOldFiles();
        }

        return _currentPath;
    }

    private void CleanOldFiles()
    {
        if (_options.RetainDays <= 0)
        {
            return;
        }

        try
        {
            var cutoff = DateTime.Now.AddDays(-_options.RetainDays);
            foreach (var file in System.IO.Directory.GetFiles(_directory, _options.FileNamePrefix + "-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
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

    public void Dispose()
    {
        _queue.CompleteAdding();
        try
        {
            _writer.Join(TimeSpan.FromSeconds(5));
        }
        catch (ThreadStateException)
        {
        }

        _queue.Dispose();
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly Action<string> _write;

    public FileLogger(string category, Action<string> write)
    {
        _category = category;
        _write = write;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Levels are filtered by the Logging configuration before reaching here.
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var builder = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Short(logLevel)).Append("] ")
            .Append(_category)
            .Append(" - ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        _write(builder.ToString());
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
