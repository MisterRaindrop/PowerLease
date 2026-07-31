using PowerLease.Persistence;
using Xunit;

namespace PowerLease.Persistence.Tests;

public sealed class PowerLeasePathsTests
{
    [Fact]
    public void Builds_all_paths_from_the_supplied_root_without_performing_io()
    {
        var root = Path.Combine(Path.GetTempPath(), "powerlease-tests", Guid.NewGuid().ToString("n"));

        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(root));

        var paths = new PowerLeasePaths(root);

        var expectedPaths = new (string Actual, string Expected)[]
        {
            (paths.Root, root),
            (paths.ConfigFilePath, Path.Combine(root, "config.json")),
            (paths.LastGoodConfigFilePath, Path.Combine(root, "config.last-good.json")),
            (paths.ConfigTempFilePath, Path.Combine(root, "config.json.tmp")),
            (paths.DatabasePath, Path.Combine(root, "data", "powerlease.db")),
            (paths.LogsDirectory, Path.Combine(root, "logs")),
            (paths.LockFilePath, Path.Combine(root, "keep-awake.lock")),
            (paths.BackupsDirectory, Path.Combine(root, "backups"))
        };

        foreach (var (actual, expected) in expectedPaths)
        {
            Assert.StartsWith(root, actual);
            Assert.Equal(expected, actual);
        }

        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(root));
    }

    [Fact]
    public void Rejects_null_empty_or_whitespace_roots()
    {
        Assert.Throws<ArgumentException>(() => new PowerLeasePaths(string.Empty));
        Assert.Throws<ArgumentException>(() => new PowerLeasePaths("   "));
        Assert.Throws<ArgumentException>(() => new PowerLeasePaths(null!));
    }

    /// <summary>
    /// On Windows this must land under ProgramData, which is the machine-wide location the service
    /// (running as SYSTEM) and the CLI (running as the logged-in user) can both reach. The test is
    /// written platform-neutrally so it also runs on the Linux CI job.
    /// </summary>
    [Fact]
    public void Default_root_sits_under_the_machine_wide_application_data_folder()
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.Equal(Path.Combine(commonAppData, "PowerLease"), PowerLeasePaths.DefaultRoot);
        Assert.Equal("PowerLease", Path.GetFileName(PowerLeasePaths.DefaultRoot));
    }

    [Fact]
    public void Default_factory_builds_paths_from_the_default_root()
    {
        var paths = PowerLeasePaths.Default();

        Assert.Equal(PowerLeasePaths.DefaultRoot, paths.Root);
        Assert.Equal(Path.Combine(PowerLeasePaths.DefaultRoot, "config.json"), paths.ConfigFilePath);
        Assert.Equal(
            Path.Combine(PowerLeasePaths.DefaultRoot, "data", "powerlease.db"),
            paths.DatabasePath);
        Assert.Equal(Path.Combine(PowerLeasePaths.DefaultRoot, "logs"), paths.LogsDirectory);
        Assert.Equal(
            Path.Combine(PowerLeasePaths.DefaultRoot, "keep-awake.lock"),
            paths.LockFilePath);
        Assert.Equal(Path.Combine(PowerLeasePaths.DefaultRoot, "backups"), paths.BackupsDirectory);
    }

    /// <summary>
    /// The lock file is a documented integration point (§10: a build script drops it to keep the
    /// machine awake), so its name is part of the contract, not an implementation detail.
    /// </summary>
    [Fact]
    public void Lock_file_is_named_keep_awake_lock_directly_under_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "powerlease-tests", Guid.NewGuid().ToString("n"));

        var paths = new PowerLeasePaths(root);

        Assert.Equal("keep-awake.lock", Path.GetFileName(paths.LockFilePath));
        Assert.Equal(root, Path.GetDirectoryName(paths.LockFilePath));
    }

    /// <summary>
    /// The three configuration paths have to be distinct files in the same directory: the atomic replace
    /// that installs a new configuration moves the temporary file into place and the file it displaces
    /// out to the last-good name, and a rename is only atomic within one volume.
    /// </summary>
    [Fact]
    public void The_three_configuration_paths_are_distinct_files_beside_each_other()
    {
        var root = Path.Combine(Path.GetTempPath(), "powerlease-tests", Guid.NewGuid().ToString("n"));

        var paths = new PowerLeasePaths(root);

        var configPaths = new[] { paths.ConfigFilePath, paths.LastGoodConfigFilePath, paths.ConfigTempFilePath };
        Assert.Equal(3, configPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(configPaths, path => Assert.Equal(root, Path.GetDirectoryName(path)));
    }
}
