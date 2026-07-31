using PowerLease.Persistence.Configuration;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class ConfigValidatorTests
{
    private static IReadOnlyList<ConfigProblem> Problems(PowerLeaseConfig config) => ConfigValidator.Validate(config);

    private static ConfigProblem Only(PowerLeaseConfig config) => Assert.Single(ConfigValidator.Validate(config));

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Empty(Problems(new PowerLeaseConfig()));
    }

    [Fact]
    public void A_schema_version_from_another_release_is_refused()
    {
        Assert.Equal("schemaVersion", Only(new PowerLeaseConfig { SchemaVersion = 2 }).Path);
    }

    [Fact]
    public void Ssh_needs_a_port_while_it_is_enabled()
    {
        Assert.Equal("ssh.ports", Only(new PowerLeaseConfig { Ssh = new SshOptions { Ports = [] } }).Path);
    }

    [Fact]
    public void Ssh_with_no_ports_is_fine_once_it_is_disabled()
    {
        Assert.Empty(Problems(new PowerLeaseConfig { Ssh = new SshOptions { Enabled = false, Ports = [] } }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_port_outside_the_valid_range_is_refused(int port)
    {
        Assert.Equal("ssh.ports[0]", Only(new PowerLeaseConfig { Ssh = new SshOptions { Ports = [port] } }).Path);
    }

    [Fact]
    public void A_port_listed_twice_is_refused()
    {
        // Two producers watching one port would report the same session twice.
        Assert.Equal("ssh.ports[1]", Only(new PowerLeaseConfig { Ssh = new SshOptions { Ports = [22, 22] } }).Path);
    }

    [Fact]
    public void A_relative_ssh_log_path_is_refused()
    {
        // The service runs as a system account, so a relative path resolves somewhere the user is not
        // looking.
        Assert.Equal(
            "ssh.fileLogPath",
            Only(new PowerLeaseConfig { Ssh = new SshOptions { FileLogPath = "logs/ssh.log" } }).Path);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_percentage_threshold_that_is_not_a_percentage_is_refused(double threshold)
    {
        var config = new PowerLeaseConfig
        {
            IdleRules = new IdleRuleOptions
            {
                Cpu = new PercentRuleOptions { Enabled = true, ThresholdPercent = threshold, DurationMinutes = 20 }
            }
        };

        Assert.Contains(Problems(config), problem => problem.Path == "idleRules.cpu.thresholdPercent");
    }

    [Fact]
    public void A_threshold_that_is_not_a_number_is_refused_because_it_would_read_as_permanently_quiet()
    {
        // NaN compares false against every measurement, so it would classify all activity as quiet and
        // release protection. It must never reach the tracker.
        var config = new PowerLeaseConfig
        {
            IdleRules = new IdleRuleOptions
            {
                Disk = new ThroughputRuleOptions
                {
                    Enabled = true,
                    ThresholdBytesPerSecond = double.NaN,
                    DurationMinutes = 15
                }
            }
        };

        Assert.Contains("finite", Only(config).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_throughput_threshold_is_refused()
    {
        var config = new PowerLeaseConfig
        {
            IdleRules = new IdleRuleOptions
            {
                Network = new ThroughputRuleOptions
                {
                    Enabled = true,
                    ThresholdBytesPerSecond = -1,
                    DurationMinutes = 15
                }
            }
        };

        Assert.Equal("idleRules.network.thresholdBytesPerSecond", Only(config).Path);
    }

    [Fact]
    public void A_zero_quiet_duration_is_refused()
    {
        // Zero would mean one quiet measurement is enough to release, which is exactly the instant
        // reading the design forbids.
        var config = new PowerLeaseConfig
        {
            IdleRules = new IdleRuleOptions
            {
                Cpu = new PercentRuleOptions { Enabled = true, ThresholdPercent = 10, DurationMinutes = 0 }
            }
        };

        Assert.Equal("idleRules.cpu.durationMinutes", Only(config).Path);
    }

    [Fact]
    public void A_relative_lock_file_path_is_refused()
    {
        var config = new PowerLeaseConfig
        {
            ProtectedProcesses = new ProtectedProcessOptions { LockFile = "keep-awake.lock" }
        };

        Assert.Equal("protectedProcesses.lockFile", Only(config).Path);
    }

    [Fact]
    public void A_blank_process_name_is_refused()
    {
        var config = new PowerLeaseConfig
        {
            ProtectedProcesses = new ProtectedProcessOptions { Names = ["msbuild", "  "] }
        };

        Assert.Equal("protectedProcesses.names[1]", Only(config).Path);
    }

    [Fact]
    public void Keeping_samples_longer_than_the_buckets_built_from_them_is_refused()
    {
        // Deleting the minute buckets first would throw away the only remaining copy of that history.
        var config = new PowerLeaseConfig
        {
            Retention = new RetentionOptions { RawHours = 48, MinuteDays = 1, EventDays = 365 }
        };

        Assert.Equal("retention.rawHours", Only(config).Path);
    }

    [Fact]
    public void An_unknown_log_level_is_refused()
    {
        Assert.Equal(
            "logging.minimumLevel",
            Only(new PowerLeaseConfig { Logging = new LoggingOptions { MinimumLevel = "Chatty" } }).Path);
    }

    [Fact]
    public void An_unknown_energy_source_is_refused()
    {
        Assert.Equal(
            "energy.source",
            Only(new PowerLeaseConfig { Energy = new EnergyOptions { Source = "Guess" } }).Path);
    }

    [Fact]
    public void A_negative_electricity_price_is_refused()
    {
        Assert.Equal(
            "energy.electricityPricePerKwh",
            Only(new PowerLeaseConfig { Energy = new EnergyOptions { ElectricityPricePerKwh = -1 } }).Path);
    }

    [Fact]
    public void Optional_energy_values_may_be_absent()
    {
        Assert.Empty(Problems(new PowerLeaseConfig
        {
            Energy = new EnergyOptions { IdleBaselineWatts = null, ElectricityPricePerKwh = null }
        }));
    }

    [Fact]
    public void A_blank_language_tag_is_refused()
    {
        Assert.Equal(
            "general.language",
            Only(new PowerLeaseConfig { General = new GeneralOptions { Language = "  " } }).Path);
    }

    [Fact]
    public void A_blank_command_line_pattern_is_refused()
    {
        // An empty pattern would match every process and hold the machine awake forever.
        var config = new PowerLeaseConfig
        {
            ProtectedProcesses = new ProtectedProcessOptions { CommandLinePatterns = ["dotnet build", ""] }
        };

        Assert.Equal("protectedProcesses.commandLinePatterns[1]", Only(config).Path);
    }

    [Fact]
    public void A_blank_currency_is_refused()
    {
        Assert.Equal(
            "energy.currency",
            Only(new PowerLeaseConfig { Energy = new EnergyOptions { Currency = "" } }).Path);
    }

    [Fact]
    public void An_energy_figure_that_is_not_a_number_is_refused()
    {
        Assert.Contains(
            "finite",
            Only(new PowerLeaseConfig { Energy = new EnergyOptions { IdleBaselineWatts = double.NaN } }).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_schedule_is_reported_by_the_validator_too()
    {
        // Not only by the converter: this is the path the loader actually takes, so a bad window has to
        // be refused here or it reaches the evaluator.
        var config = new PowerLeaseConfig
        {
            Schedules = [new ScheduleWindowOptions { Start = "09:00", End = "09:00", Days = ["Monday"] }]
        };

        Assert.Equal("schedules[0]", Only(config).Path);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var config = new PowerLeaseConfig
        {
            SchemaVersion = 99,
            Ssh = new SshOptions { Ports = [0] },
            Logging = new LoggingOptions { MinimumLevel = "Chatty", RetentionDays = 0 }
        };

        Assert.Equal(4, Problems(config).Count);
    }

    [Fact]
    public void A_null_configuration_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigValidator.Validate(null!));
    }
}
