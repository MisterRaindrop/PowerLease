using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Application;
using PowerLease.Service;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class HeartbeatWorkerTests
{
    [Fact]
    public async Task Worker_logs_heartbeats_and_stops_cleanly_when_cancelled()
    {
        var logger = new RecordingLogger();
        var worker = new HeartbeatWorker(logger, new SystemClock(), TimeSpan.FromMilliseconds(20));
        using var cancellationSource = new CancellationTokenSource();

        await worker.StartAsync(cancellationSource.Token);

        try
        {
            await WaitForHeartbeatsAsync(logger, requiredCount: 2, TimeSpan.FromSeconds(2));

            cancellationSource.Cancel();
            await StopWorkerAsync(worker);

            var countAfterStop = logger.HeartbeatCount;
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

            Assert.True(logger.HeartbeatCount >= 2, "The worker must produce at least two heartbeats.");
            Assert.Equal(countAfterStop, logger.HeartbeatCount);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            cancellationSource.Cancel();
            await StopWorkerAsync(worker);
            worker.Dispose();
        }
    }

    private static async Task StopWorkerAsync(HeartbeatWorker worker)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await worker.StopAsync(timeoutSource.Token);
    }

    private static async Task WaitForHeartbeatsAsync(
        RecordingLogger logger,
        int requiredCount,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (logger.HeartbeatCount < requiredCount && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(
            logger.HeartbeatCount >= requiredCount,
            $"Timed out after {timeout} waiting for {requiredCount} heartbeats; received {logger.HeartbeatCount}.");
    }

    private sealed class RecordingLogger : ILogger<HeartbeatWorker>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public int HeartbeatCount
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Count(entry =>
                        entry.Level == LogLevel.Information &&
                        entry.Message.StartsWith("Heartbeat", StringComparison.Ordinal));
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
