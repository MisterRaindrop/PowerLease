using PowerLease.Persistence.Configuration;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class JsonConfigStoreTests
{
    [Fact]
    public void A_first_run_with_no_file_uses_defaults_and_is_not_a_fault()
    {
        using var root = new TempRoot();

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        Assert.Empty(result.Problems);
        Assert.False(result.RequiresPersistentFault);
        Assert.Equal(PowerLeaseConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
    }

    [Fact]
    public void A_saved_configuration_reads_back_unchanged()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        var config = new PowerLeaseConfig
        {
            General = new GeneralOptions { Language = "zh-CN", ResumeGracePeriodMinutes = 7 },
            Ssh = new SshOptions { Ports = [22, 2222], TcpOnlyConfirmed = true, DefaultHoldMinutes = 90 },
            Schedules = [new ScheduleWindowOptions { Start = "22:00", End = "06:00", Days = ["Friday"] }],
            Retention = new RetentionOptions { RawHours = 12, MinuteDays = 7, EventDays = 90 }
        };

        store.Save(config);
        var result = store.Load();

        Assert.Equal(ConfigSource.File, result.Source);
        Assert.Empty(result.Problems);

        // Compared through the serialised form rather than with ==, because these records hold
        // collections and record equality compares those by reference. Re-saving what was loaded and
        // requiring the bytes to match is also a stronger check: it catches a property that failed to
        // serialise at all, which a reference comparison would pass.
        using var reference = new TempRoot();
        new JsonConfigStore(reference.Paths).Save(result.Config);
        Assert.Equal(
            File.ReadAllText(root.Paths.ConfigFilePath),
            File.ReadAllText(reference.Paths.ConfigFilePath));

        Assert.Equal("zh-CN", result.Config.General.Language);
        Assert.Equal(7, result.Config.General.ResumeGracePeriodMinutes);
        Assert.Equal([22, 2222], result.Config.Ssh.Ports);
        Assert.True(result.Config.Ssh.TcpOnlyConfirmed);
        Assert.Equal(90, result.Config.Ssh.DefaultHoldMinutes);
        Assert.Equal(12, result.Config.Retention.RawHours);
        var schedule = Assert.Single(result.Config.Schedules);
        Assert.Equal("22:00", schedule.Start);
        Assert.Equal(["Friday"], schedule.Days);
    }

    [Fact]
    public void The_default_configuration_is_valid()
    {
        // Anything else would mean the service cannot start on a clean machine.
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);

        store.Save(new PowerLeaseConfig());

        Assert.Equal(ConfigSource.File, store.Load().Source);
    }

    [Fact]
    public void Saving_again_leaves_the_previous_file_as_the_last_good_one()
    {
        // The fallback is only trustworthy because the displaced file becomes the backup in the same
        // atomic operation, so the last good file is always one that was actually live.
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);

        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "en-US" } });
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "zh-CN" } });

        Assert.Contains("zh-CN", File.ReadAllText(root.Paths.ConfigFilePath), StringComparison.Ordinal);
        Assert.Contains("en-US", File.ReadAllText(root.Paths.LastGoodConfigFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_the_last_good_one_and_says_so()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "en-US" } });
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "zh-CN" } });

        File.WriteAllText(root.Paths.ConfigFilePath, "{ this is not json");
        var result = store.Load();

        Assert.Equal(ConfigSource.LastGood, result.Source);
        Assert.Equal("en-US", result.Config.General.Language);
        Assert.True(result.RequiresPersistentFault);
        Assert.Contains("JSON", result.ProblemSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_file_with_no_last_good_one_falls_back_to_defaults_and_still_reports_it()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, "{ this is not json");

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        Assert.True(result.RequiresPersistentFault);
    }

    [Fact]
    public void A_deleted_file_falls_back_to_the_last_good_one_rather_than_to_defaults()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "en-US" } });
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "zh-CN" } });

        File.Delete(root.Paths.ConfigFilePath);
        var result = store.Load();

        Assert.Equal(ConfigSource.LastGood, result.Source);
        Assert.Equal("en-US", result.Config.General.Language);
        Assert.True(result.RequiresPersistentFault);
    }

    [Fact]
    public void A_misspelled_field_is_rejected_rather_than_ignored()
    {
        // A key that silently does nothing is the failure mode this rejects: the user believes a
        // threshold is in effect while the default is running.
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, """{ "schemaVersion": 1, "sshh": { "enabled": false } }""");

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        Assert.True(result.RequiresPersistentFault);
    }

    [Theory]
    [InlineData("general.dryRun", "true")]
    [InlineData("idleRules.candidateObservationMinutes", "5")]
    [InlineData("idleRules.preSleepCountdownSeconds", "60")]
    [InlineData("idleRules.userInput", """{ "enabled": true }""")]
    [InlineData("powerPolicy", """{ "mode": "SmartTwoStage" }""")]
    [InlineData("wake", """{ "scheduledWakeEnabled": true }""")]
    [InlineData("temperature", """{ "enabled": true }""")]
    [InlineData("notifications", """{ "desktopEnabled": true }""")]
    public void A_field_this_version_does_not_have_is_rejected_with_an_explanation(string path, string value)
    {
        // Accepting one of these silently would let a user believe the machine will be put to sleep,
        // or that a countdown will appear, when nothing of the kind is implemented.
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, BuildJson(path, value));

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(path, problem.Path);
        Assert.True(
            problem.Message.StartsWith("Removed.", StringComparison.Ordinal)
            || problem.Message.StartsWith("Not in this version.", StringComparison.Ordinal),
            $"Expected an explanation of what happened to the setting, got: {problem.Message}");
    }

    [Fact]
    public void Every_catalogued_removed_field_is_covered_by_the_rejection_test()
    {
        // Guards against a path being added to the catalogue without a test proving it is refused.
        Assert.Equal(
            [
                "general.dryRun",
                "idleRules.candidateObservationMinutes",
                "idleRules.preSleepCountdownSeconds",
                "idleRules.userInput",
                "powerPolicy",
                "wake",
                "temperature",
                "notifications"
            ],
            RemovedConfigFields.Paths);
    }

    [Fact]
    public void Sections_left_out_of_the_file_take_their_defaults()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, """{ "schemaVersion": 1, "ssh": { "enabled": false } }""");

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.File, result.Source);
        Assert.False(result.Config.Ssh.Enabled);
        Assert.Equal([22], result.Config.Ssh.Ports);
        Assert.Equal(20, result.Config.IdleRules.Cpu.DurationMinutes);
    }

    [Fact]
    public void An_invalid_configuration_is_refused_before_anything_is_written()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "en-US" } });
        var before = File.ReadAllText(root.Paths.ConfigFilePath);

        var error = Assert.Throws<ConfigValidationException>(() => store.Save(new PowerLeaseConfig
        {
            Ssh = new SshOptions { Ports = [70000] }
        }));

        Assert.NotEmpty(error.Problems);
        Assert.Equal(before, File.ReadAllText(root.Paths.ConfigFilePath));
        Assert.False(File.Exists(root.Paths.ConfigTempFilePath));
    }

    [Fact]
    public void A_temporary_file_left_by_an_interrupted_write_is_never_read_as_configuration()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "zh-CN" } });
        File.WriteAllText(root.Paths.ConfigTempFilePath, "half a file, written before the power cut");

        var result = store.Load();

        Assert.Equal(ConfigSource.File, result.Source);
        Assert.Equal("zh-CN", result.Config.General.Language);
    }

    [Fact]
    public void A_leftover_temporary_file_does_not_break_the_next_save()
    {
        using var root = new TempRoot();
        var store = new JsonConfigStore(root.Paths);
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigTempFilePath, "half a file, written before the power cut");

        store.Save(new PowerLeaseConfig { General = new GeneralOptions { Language = "en-US" } });

        Assert.Equal(ConfigSource.File, store.Load().Source);
        Assert.False(File.Exists(root.Paths.ConfigTempFilePath));
    }

    [Fact]
    public void Saving_creates_the_data_directory()
    {
        using var root = new TempRoot();
        Assert.False(Directory.Exists(root.Path));

        new JsonConfigStore(root.Paths).Save(new PowerLeaseConfig());

        Assert.True(File.Exists(root.Paths.ConfigFilePath));
    }

    [Fact]
    public void A_file_holding_only_null_is_rejected()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, "null");

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        Assert.True(result.RequiresPersistentFault);
    }

    [Fact]
    public void Loading_reports_every_problem_at_once()
    {
        // One error per restart is how a user ends up deleting the file instead of fixing it.
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, """
            {
              "schemaVersion": 1,
              "ssh": { "ports": [70000] },
              "logging": { "minimumLevel": "Chatty" },
              "energy": { "source": "Guess" }
            }
            """);

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(3, result.Problems.Count);
        Assert.Contains(result.Problems, problem => problem.Path == "ssh.ports[0]");
        Assert.Contains(result.Problems, problem => problem.Path == "logging.minimumLevel");
        Assert.Contains(result.Problems, problem => problem.Path == "energy.source");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        // People edit this file by hand, and refusing a trailing comma would be a fault latched over
        // punctuation.
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.Paths.ConfigFilePath, """
            {
              // Keep the machine awake for SSH only.
              "schemaVersion": 1,
              "ssh": { "enabled": true, },
            }
            """);

        Assert.Equal(ConfigSource.File, new JsonConfigStore(root.Paths).Load().Source);
    }

    [Fact]
    public void Something_that_is_not_a_readable_file_is_reported_rather_than_thrown()
    {
        // Loading must never throw, whatever is on disk. Refusing to start would leave the machine with
        // no keep-awake protection at all, which is the outcome the product exists to prevent.
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Paths.ConfigFilePath);

        var result = new JsonConfigStore(root.Paths).Load();

        Assert.Equal(ConfigSource.Defaults, result.Source);
        Assert.True(result.RequiresPersistentFault);
        Assert.NotEmpty(result.ProblemSummary);
    }

    [Fact]
    public void A_validation_failure_reports_the_problems_it_found()
    {
        var error = new ConfigValidationException([new ConfigProblem("ssh.ports[0]", "not a port")]);

        Assert.Contains("ssh.ports[0]", error.Message, StringComparison.Ordinal);
        Assert.Single(error.Problems);

        // The message-only form is what a caller without a problem list gets.
        var plain = new ConfigValidationException("something else went wrong");
        Assert.Equal("something else went wrong", plain.Message);
        Assert.Empty(plain.Problems);
        Assert.Empty(new ConfigValidationException().Problems);

        var wrapped = new ConfigValidationException("outer", new InvalidOperationException("inner"));
        Assert.Equal("inner", wrapped.InnerException?.Message);
    }

    [Fact]
    public void The_paths_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonConfigStore(null!));
    }

    [Fact]
    public void A_null_configuration_cannot_be_saved()
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentNullException>(() => new JsonConfigStore(root.Paths).Save(null!));
    }

    /// <summary>Build the smallest valid document that contains <paramref name="dottedPath" />.</summary>
    private static string BuildJson(string dottedPath, string value)
    {
        var segments = dottedPath.Split('.');
        var json = value;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            json = $"{{ \"{segments[i]}\": {json} }}";
        }

        // Splice the built object into a document that is otherwise valid, so the only thing wrong
        // with it is the field under test.
        return $"{{ \"schemaVersion\": 1, {json.Trim()[1..^1].Trim()} }}";
    }
}
