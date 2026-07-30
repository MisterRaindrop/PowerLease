using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Application;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows;

namespace PowerLease.Service;

public static class Program
{
    private static readonly Action<ILogger, string, Exception?> LogStarting =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, "Starting"),
            "PowerLease starting on {Platform}");

    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ITimeZoneProvider, SystemTimeZoneProvider>();
        builder.Services.AddHostedService(sp => new HeartbeatWorker(
            sp.GetRequiredService<ILogger<HeartbeatWorker>>(),
            sp.GetRequiredService<IClock>(),
            TimeSpan.FromSeconds(30)));
        builder.Services.AddWindowsService(options => options.ServiceName = "PowerLease");

        var host = builder.Build();
        LogStarting(
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PowerLease.Service"),
            WindowsPlatform.Describe(),
            null);
        host.Run();
    }
}
