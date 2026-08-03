using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using PowerLease.Application;
using PowerLease.Application.Hosting;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Infrastructure.Windows.Sources;
using PowerLease.Persistence;
using PowerLease.Persistence.Configuration;
using PowerLease.Persistence.History;
using PowerLease.Persistence.Sqlite;

namespace PowerLease.Service;

public static class Program
{
    private static readonly Action<ILogger, string, Exception?> LogStarting =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, "Starting"),
            "PowerLease starting on {Platform}");

    private static readonly Action<ILogger, string, Exception?> LogFatal =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(2, "FatalStartupFailure"),
            "PowerLease could not start: {Message}");

    public static async Task<int> Main(string[] args)
    {
        PowerLeasePaths? paths = null;
        try
        {
            paths = PowerLeasePaths.Default();
            Directory.CreateDirectory(paths.Root);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
            Directory.CreateDirectory(paths.LogsDirectory);

            var clock = new SystemClock();
            var configLoad = new JsonConfigStore(paths).Load();
            _ = new MigrationRunner(paths, clock).Run();

            using var store = new SqliteHistoryStore(new SqliteConnectionFactory(paths.DatabasePath));
            var loaded = store.LoadLeases();
            var catalog = ProducerCatalog.Create(configLoad.Config);
            var options = ServiceComposition.BuildKernelOptions(configLoad.Config, catalog);
            var power = new PowerRequestManager();
            var timeZones = new SystemTimeZoneProvider();
            var kernel = new InhibitKernel(options, new PowerInhibitCoordinator(power), clock);
            _ = kernel.Restore(loaded.Leases);
            var loop = new KernelLoop(
                kernel,
                new SynchronizedEffectExecutor(new SqliteEffectExecutor(store, clock), store),
                timeZones);

            PostStartupMessages(loop, configLoad, loaded);

            using var host = CreateHost(
                args,
                paths,
                configLoad.Config,
                catalog,
                loaded,
                clock,
                store,
                power,
                timeZones,
                loop,
                options);

            LogStarting(
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PowerLease.Service"),
                WindowsPlatform.Describe(),
                null);

            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (MigrationException error)
        {
            LogStartupFailure(paths, error);
            return 1;
        }
#pragma warning disable CA1031 // Startup failures must be logged and converted to a non-zero exit, not a crash loop.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogStartupFailure(paths, error);
            return 1;
        }
    }

    private static IHost CreateHost(
        string[] args,
        PowerLeasePaths paths,
        PowerLeaseConfig config,
        ProducerCatalog catalog,
        LeaseLoadResult loaded,
        IClock clock,
        SqliteHistoryStore store,
        PowerRequestManager power,
        SystemTimeZoneProvider timeZones,
        KernelLoop loop,
        KernelOptions kernelOptions)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => options.ServiceName = "PowerLease")
            .ConfigureLogging(logging =>
            {
                if (Enum.TryParse<LogLevel>(config.Logging.MinimumLevel, ignoreCase: true, out var minimum))
                {
                    logging.SetMinimumLevel(minimum);
                }

                logging.AddProvider(new DailyFileLoggerProvider(
                    paths.LogsDirectory,
                    TimeSpan.FromDays(config.Logging.RetentionDays)));
            })
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(paths);
                services.AddSingleton(config);
                services.AddSingleton(catalog);
                services.AddSingleton(loaded);
                services.AddSingleton<IClock>(clock);
                services.AddSingleton(store);
                services.AddSingleton(power);
                services.AddSingleton(timeZones);
                services.AddSingleton<ITimeZoneProvider>(timeZones);
                services.AddSingleton(loop);
                services.AddSingleton(kernelOptions);
                services.AddSingleton<StoreGate>();
                services.AddSingleton<LeaseCommandDispatcher>();
                services.AddSingleton<PowerEventRelay>();
                services.AddSingleton<IPowerCapabilityProbe, PowerCapabilityProbe>();
                services.AddSingleton<IpcRequestRouter>();

                services.AddSingleton<IHostedService, LifecycleWorker>();
                services.AddSingleton<IHostedService, InhibitKernelWorker>();
                ServiceComposition.AddProducerServices(services, catalog);
                services.AddSingleton<IHostedService, PipeServerWorker>();
                services.AddSingleton<IHostedService, HousekeepingWorker>();

                if (WindowsServiceHelpers.IsWindowsService())
                {
                    // Registered after UseWindowsService so the lifetime that also surfaces power events wins.
                    services.AddSingleton<IHostLifetime, PowerEventWindowsServiceLifetime>();
                }
            });

        return builder.Build();
    }

    private static void PostStartupMessages(
        KernelLoop loop,
        ConfigLoadResult configLoad,
        LeaseLoadResult loaded)
    {
        loop.Post(new ServiceStarted("the Windows service started"));

        if (configLoad.RequiresPersistentFault)
        {
            loop.Post(new FaultObserved(
                "configuration",
                FaultSeverity.Persistent,
                configLoad.ProblemSummary));
        }

        if (loaded.HasUnreadableRows)
        {
            loop.Post(new FaultObserved(
                "leases-unreadable",
                FaultSeverity.Persistent,
                "Stored leases could not be read: " + string.Join("; ", loaded.UnreadableRows)));
        }
    }

    private static void LogStartupFailure(PowerLeasePaths? paths, Exception error)
    {
        try
        {
            using var factory = LoggerFactory.Create(logging =>
            {
                logging.AddSimpleConsole();
                if (paths is not null)
                {
                    logging.AddProvider(new DailyFileLoggerProvider(paths.LogsDirectory, TimeSpan.FromDays(30)));
                }
            });

            LogFatal(factory.CreateLogger("PowerLease.Service"), error.Message, error);
        }
#pragma warning disable CA1031 // If logging itself is unavailable, stderr is the last reliable startup report.
        catch (Exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"PowerLease could not start: {error}");
        }
    }
}
