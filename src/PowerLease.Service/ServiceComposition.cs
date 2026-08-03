using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Persistence.Configuration;

namespace PowerLease.Service;

internal enum ProducerKind
{
    Ssh,
    Activity,
    Processes,
    LockFile,
    Schedule
}

internal sealed record ProducerDefinition(string SourceId, ProducerKind Kind, InhibitorKind CoveredKind);

internal sealed class ProducerCatalog
{
    private ProducerCatalog(IReadOnlyList<ProducerDefinition> producers)
    {
        Producers = producers;
        ExpectedSources = producers.Select(producer => producer.SourceId).ToArray();
        CoveredKinds = producers.Select(producer => producer.CoveredKind).ToArray();
    }

    public IReadOnlyList<ProducerDefinition> Producers { get; }

    public IReadOnlyList<string> ExpectedSources { get; }

    public IReadOnlyList<InhibitorKind> CoveredKinds { get; }

    public static ProducerCatalog Create(PowerLeaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var producers = new List<ProducerDefinition>();

        if (config.Ssh.Enabled)
        {
            producers.Add(new(SshSessionCorrelator.SourceId, ProducerKind.Ssh, InhibitorKind.SshSession));
        }

        var activity = ServiceComposition.CreateActivityEvaluator(config.IdleRules);
        if (activity.HasEnabledRules)
        {
            producers.Add(new(SystemActivityEvaluator.SourceId, ProducerKind.Activity, InhibitorKind.SystemActivity));
        }

        if (config.ProtectedProcesses.Names.Count > 0
            || config.ProtectedProcesses.CommandLinePatterns.Count > 0)
        {
            producers.Add(new(
                ProtectedProcessEvaluator.SourceId,
                ProducerKind.Processes,
                InhibitorKind.ProtectedProcess));
        }

        // Null in configuration means the documented default path, not disabled.
        producers.Add(new(LockFileEvaluator.SourceId, ProducerKind.LockFile, InhibitorKind.LockFile));

        // An empty schedule is still a healthy producer that positively reports absence.
        producers.Add(new(ScheduleEvaluator.SourceId, ProducerKind.Schedule, InhibitorKind.ScheduleWindow));
        return new ProducerCatalog(producers);
    }
}

internal static class ServiceComposition
{
    public static KernelOptions BuildKernelOptions(PowerLeaseConfig config, ProducerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(catalog);

        return new KernelOptions
        {
            ResumeGracePeriod = TimeSpan.FromMinutes(config.General.ResumeGracePeriodMinutes),
            StartupGracePeriod = TimeSpan.FromMinutes(config.General.ServiceRecoveryGracePeriodMinutes),
            ExpectedSources = catalog.ExpectedSources,
            CoveredKinds =
            [
                .. catalog.CoveredKinds,
                InhibitorKind.CliLease,
                InhibitorKind.GracePeriod,
                InhibitorKind.Fault,
                InhibitorKind.ProducerUnhealthy
            ]
        };
    }

    public static void AddProducerServices(IServiceCollection services, ProducerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var producer in catalog.Producers)
        {
            services.AddSingleton(typeof(IHostedService), producer.Kind switch
            {
                ProducerKind.Ssh => typeof(SshProducerWorker),
                ProducerKind.Activity => typeof(ActivityProducerWorker),
                ProducerKind.Processes => typeof(ProcessProducerWorker),
                ProducerKind.LockFile => typeof(LockFileProducerWorker),
                ProducerKind.Schedule => typeof(ScheduleProducerWorker),
                _ => throw new InvalidOperationException($"Unknown producer kind '{producer.Kind}'.")
            });
        }
    }

    public static SystemActivityEvaluator CreateActivityEvaluator(IdleRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SystemActivityEvaluator(new SystemActivityOptions
        {
            Cpu = Activity(options.Cpu),
            Memory = Activity(options.Memory),
            Disk = Activity(options.Disk),
            Network = Activity(options.Network)
        });
    }

    private static ActivityRule Activity(PercentRuleOptions options) =>
        new(options.Enabled, options.ThresholdPercent, TimeSpan.FromMinutes(options.DurationMinutes));

    private static ActivityRule Activity(ThroughputRuleOptions options) =>
        new(options.Enabled, options.ThresholdBytesPerSecond, TimeSpan.FromMinutes(options.DurationMinutes));
}
