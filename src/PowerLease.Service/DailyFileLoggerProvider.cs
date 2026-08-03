using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PowerLease.Service;

internal sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _sync = new();

    public DailyFileLoggerProvider(string directory, TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteExpiredLogs(retention);
    }

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{now:O} [{level}] {category} ({eventId.Id}:{eventId.Name}) {message}");
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        line += Environment.NewLine;

        try
        {
            lock (_sync)
            {
                File.AppendAllText(
                    Path.Combine(_directory, $"powerlease-{now.UtcDateTime:yyyy-MM-dd}.log"),
                    line);
            }
        }
#pragma warning disable CA1031 // Logging must never become the failure that stops protection.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void DeleteExpiredLogs(TimeSpan retention)
    {
        try
        {
            var cutoff = DateTime.UtcNow - retention;
            foreach (var path in Directory.EnumerateFiles(_directory, "powerlease-*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
#pragma warning disable CA1031 // Log retention is best-effort and cannot prevent the service starting.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private sealed class DailyFileLogger : ILogger
    {
        private readonly DailyFileLoggerProvider _provider;
        private readonly string _category;

        public DailyFileLogger(DailyFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                _provider.Write(_category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
