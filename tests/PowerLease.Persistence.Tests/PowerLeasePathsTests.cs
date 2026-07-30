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
}
